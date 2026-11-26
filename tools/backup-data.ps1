param(
  [string]$Destination = '',
  [string]$PinFile = '',
  [int]$Port = 5080,
  [int]$RetentionDays = 30
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$root = Split-Path -Parent $PSScriptRoot
$backupRoot = if ([string]::IsNullOrWhiteSpace($Destination)) { Join-Path $root 'backups\data-backups' } else { [IO.Path]::GetFullPath($Destination) }
$timestamp = Get-Date -Format 'yyyyMMdd_HHmmss'
$target = Join-Path $backupRoot $timestamp
$workRoot = if ($backupRoot.StartsWith($root,[StringComparison]::OrdinalIgnoreCase)) { $backupRoot } else { Join-Path ([IO.Path]::GetTempPath()) 'MafLibrary-backups' }
$work = Join-Path $workRoot ".MafLibrary-backup-$([guid]::NewGuid().ToString('N'))"
$zipPath = Join-Path $work 'server-backup.zip'
$validationData = Join-Path $work 'verify-data'

function Get-BackupPin {
  if ($env:MAF_ADMIN_PIN) { return $env:MAF_ADMIN_PIN }
  if ($PinFile) {
    if (-not (Test-Path -LiteralPath $PinFile)) { throw "Protected PIN file not found: $PinFile" }
    $secure = ConvertTo-SecureString -String ([IO.File]::ReadAllText($PinFile))
    $ptr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
    try { return [Runtime.InteropServices.Marshal]::PtrToStringBSTR($ptr) }
    finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($ptr) }
  }
  $secure = Read-Host 'Library edit PIN' -AsSecureString
  $ptr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
  try { return [Runtime.InteropServices.Marshal]::PtrToStringBSTR($ptr) }
  finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($ptr) }
}

function Get-ZipHashes([string]$Path) {
  Add-Type -AssemblyName System.IO.Compression.FileSystem
  $archive = [IO.Compression.ZipFile]::OpenRead($Path)
  try {
    $hashes = [ordered]@{}
    foreach ($entry in $archive.Entries | Where-Object { -not [string]::IsNullOrEmpty($_.Name) } | Sort-Object FullName) {
      $stream = $entry.Open()
      $sha = [Security.Cryptography.SHA256]::Create()
      try { $hash = [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-','').ToLowerInvariant() }
      finally { $sha.Dispose(); $stream.Dispose() }
      $hashes[$entry.FullName] = [pscustomobject]@{ Sha256 = $hash; Size = [long]$entry.Length }
    }
    return $hashes
  }
  finally { $archive.Dispose() }
}

try {
  New-Item -ItemType Directory -Force -Path $work,$backupRoot | Out-Null
  $uri = "http://127.0.0.1:$Port/api/backup.zip"
  $pin = Get-BackupPin
  Invoke-WebRequest -Uri $uri -Headers @{ 'X-Edit-Pin' = $pin } -OutFile $zipPath -TimeoutSec 1800 -UseBasicParsing
  if ((Get-Item -LiteralPath $zipPath).Length -lt 1024) { throw 'Backup archive is unexpectedly small.' }

  $hashes = Get-ZipHashes $zipPath
  foreach ($required in @('library.db','library.json','library.json.old')) {
    if (-not $hashes.Contains($required)) { throw "Required file missing from backup archive: $required" }
  }
  if (@($hashes.Keys | Where-Object { $_ -match '^uploads/(?!\.staging/)' }).Count -ne $hashes.Count - 3) {
    throw 'Backup contains unexpected upload paths (staging data is never backed up).'
  }
  New-Item -ItemType Directory -Force -Path $validationData,(Join-Path $validationData 'uploads') | Out-Null
  [IO.Compression.ZipFile]::ExtractToDirectory($zipPath,$validationData)

  $exeCandidates = @(
    (Join-Path $root 'bin\Release\net10.0\MafLibrary.exe'),
    (Join-Path $root 'bin\Debug\net10.0\MafLibrary.exe'),
    (Join-Path $root 'MafLibrary.exe')
  )
  $exe = $exeCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
  if (-not $exe) { throw 'MafLibrary.exe was not found. Build or publish the application before backing up.' }
  $oldData = $env:MAF_DATA
  try {
    $env:MAF_DATA = $validationData
    $check = & $exe --check-library 2>&1
    $checkExit = $LASTEXITCODE
    $check | ForEach-Object { Write-Output $_ }
    if ($checkExit -ne 0) { throw "Backup database/assets validation failed with exit code $checkExit." }
  }
  finally { $env:MAF_DATA = $oldData }

  New-Item -ItemType Directory -Force -Path $target | Out-Null
  Copy-Item -LiteralPath $zipPath -Destination (Join-Path $target 'library-backup.zip')
  $manifest = [ordered]@{
    Format = 'MafLibraryDataBackupV1'
    CreatedUtc = [DateTime]::UtcNow.ToString('o')
    SourceProject = [IO.Path]::GetFullPath($root)
    Archive = 'library-backup.zip'
    ArchiveSize = (Get-Item -LiteralPath (Join-Path $target 'library-backup.zip')).Length
    ArchiveSha256 = (Get-FileHash -LiteralPath (Join-Path $target 'library-backup.zip') -Algorithm SHA256).Hash.ToLowerInvariant()
    Contents = $hashes
    Validation = 'SQLite integrity, foreign keys, referenced assets and asset sizes passed'
  }
  $manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $target 'manifest.json') -Encoding UTF8

  if ($RetentionDays -gt 0) {
    Get-ChildItem -LiteralPath $backupRoot -Directory | Where-Object { $_.Name -match '^\d{8}_\d{6}$' -and $_.LastWriteTimeUtc -lt [DateTime]::UtcNow.AddDays(-$RetentionDays) } | ForEach-Object {
      if (Test-Path -LiteralPath (Join-Path $_.FullName 'manifest.json')) { Remove-Item -LiteralPath $_.FullName -Recurse -Force }
    }
  }
  Write-Output "Verified data backup created: $target"
  Write-Output "Archive size: $([math]::Round($manifest.ArchiveSize / 1MB, 2)) MiB; archived entries: $($hashes.Count)"
}
finally {
  if (Test-Path -LiteralPath $work) { Remove-Item -LiteralPath $work -Recurse -Force }
}
