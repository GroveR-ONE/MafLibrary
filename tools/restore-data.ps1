param(
  [string]$BackupPath = '',
  [string]$DataDirectory = '',
  [switch]$Confirm
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$dataRoot = if ($DataDirectory) { [IO.Path]::GetFullPath($DataDirectory) } else { Join-Path $root 'data' }
$backupRoot = Join-Path $root 'backups\data-backups'
$work = Join-Path ([IO.Path]::GetTempPath()) "MafLibrary-restore-$([guid]::NewGuid().ToString('N'))"
$stageRoot = Join-Path (Split-Path -Parent $dataRoot) ".MafLibrary-restore-stage-$([guid]::NewGuid().ToString('N'))"
$stagedData = Join-Path $stageRoot 'data'
$liveDataPath = [IO.Path]::GetFullPath((Join-Path $root 'data'))
$isLiveRestore = $dataRoot.Equals($liveDataPath,[StringComparison]::OrdinalIgnoreCase)
$rollbackRoot = if ($isLiveRestore) { Join-Path $root 'backups\pre-restore' } else { Join-Path (Split-Path -Parent $dataRoot) 'pre-restore' }
$rollback = Join-Path $rollbackRoot (Get-Date -Format 'yyyyMMdd_HHmmss')

try {
  if (-not $BackupPath) {
    $latest = Get-ChildItem -LiteralPath $backupRoot -Directory -ErrorAction SilentlyContinue |
      Where-Object { $_.Name -match '^\d{8}_\d{6}$' -and (Test-Path -LiteralPath (Join-Path $_.FullName 'manifest.json')) } |
      Sort-Object Name -Descending | Select-Object -First 1
    if (-not $latest) { throw "No verified backup found under $backupRoot" }
    $BackupPath = $latest.FullName
  }
  $BackupPath = [IO.Path]::GetFullPath($BackupPath)
  if (Test-Path -LiteralPath $BackupPath -PathType Container) {
    $manifestPath = Join-Path $BackupPath 'manifest.json'
    $archivePath = Join-Path $BackupPath 'library-backup.zip'
  } else {
    $archivePath = $BackupPath
    $manifestPath = Join-Path (Split-Path -Parent $BackupPath) 'manifest.json'
  }
  if (-not (Test-Path -LiteralPath $manifestPath) -or -not (Test-Path -LiteralPath $archivePath)) { throw 'Backup ZIP or its manifest is missing.' }
  $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
  if ($manifest.Format -ne 'MafLibraryDataBackupV1') { throw 'Unsupported backup format.' }
  $archiveHash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
  if ($archiveHash -ne $manifest.ArchiveSha256) { throw 'Backup ZIP SHA-256 does not match its manifest.' }

  Add-Type -AssemblyName System.IO.Compression.FileSystem
  $archive = [IO.Compression.ZipFile]::OpenRead($archivePath)
  try {
    foreach ($entry in $archive.Entries | Where-Object { -not [string]::IsNullOrEmpty($_.Name) }) {
      $expected = $manifest.Contents.PSObject.Properties[$entry.FullName]
      if (-not $expected) { throw "Unexpected archive entry: $($entry.FullName)" }
      if ([long]$expected.Value.Size -ne $entry.Length) { throw "Archive entry size mismatch: $($entry.FullName)" }
      $stream = $entry.Open(); $sha = [Security.Cryptography.SHA256]::Create()
      try { $actual = [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-','').ToLowerInvariant() }
      finally { $sha.Dispose(); $stream.Dispose() }
      if ($actual -ne $expected.Value.Sha256) { throw "Archive entry SHA-256 mismatch: $($entry.FullName)" }
    }
    $manifestEntryCount = @($manifest.Contents.PSObject.Properties).Count
    if ($archive.Entries.Count -ne $manifestEntryCount) { throw "The archive entry count differs from its manifest (archive=$($archive.Entries.Count), manifest=$manifestEntryCount)." }
  }
  finally { $archive.Dispose() }

  New-Item -ItemType Directory -Force -Path $work,(Split-Path -Parent $dataRoot),$stageRoot,$stagedData,(Join-Path $stagedData 'uploads') | Out-Null
  [IO.Compression.ZipFile]::ExtractToDirectory($archivePath,$stagedData)
  foreach ($required in @('library.db','library.json','library.json.old')) {
    if (-not (Test-Path -LiteralPath (Join-Path $stagedData $required))) { throw "Required restore file missing: $required" }
  }

  $exeCandidates = @(
    (Join-Path $root 'bin\Release\net10.0\MafLibrary.exe'),
    (Join-Path $root 'bin\Debug\net10.0\MafLibrary.exe'),
    (Join-Path $root 'MafLibrary.exe')
  )
  $exe = $exeCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
  if (-not $exe) { throw 'MafLibrary.exe was not found; cannot validate restored data.' }
  $oldData = $env:MAF_DATA
  try {
    $env:MAF_DATA = $stagedData
    $check = & $exe --check-library 2>&1
    $checkExit = $LASTEXITCODE
    $check | ForEach-Object { Write-Output $_ }
    if ($checkExit -ne 0) { throw 'Restored database/assets failed application validation.' }
  }
  finally { $env:MAF_DATA = $oldData }

  if ($isLiveRestore) {
  $running = Get-CimInstance Win32_Process -Filter "name='MafLibrary.exe' or name='dotnet.exe'" -ErrorAction SilentlyContinue |
      Where-Object { $_.CommandLine -match 'MafLibrary\.exe' -or ($_.CommandLine -match 'dotnet\.exe' -and $_.CommandLine -match 'MafLibrary(\.dll|\.csproj)|dotnet run --no-launch-profile') }
    if ($running) { throw 'The library server appears to be running. Stop it, then rerun restore; the live data was not changed.' }
  }

  Write-Output "Verified backup: $BackupPath"
  Write-Output "Items / assets: $((Get-Content (Join-Path $stagedData 'library.json') -Raw | ConvertFrom-Json).items.Count) items; asset files in archive: $((Get-ChildItem (Join-Path $stagedData 'uploads') -File -Recurse).Count)"
  if (-not $Confirm) {
    $answer = Read-Host "RESTORE will replace '$dataRoot'. A rollback copy will be saved at '$rollback'. Type RESTORE to continue"
    if ($answer -cne 'RESTORE') { Write-Output 'Restore cancelled; live data unchanged.'; exit 0 }
  }

  if (Test-Path -LiteralPath $dataRoot) {
    New-Item -ItemType Directory -Force -Path $rollbackRoot | Out-Null
    Move-Item -LiteralPath $dataRoot -Destination $rollback
  }
  try {
    Move-Item -LiteralPath $stagedData -Destination $dataRoot
  }
  catch {
    if (-not (Test-Path -LiteralPath $dataRoot) -and (Test-Path -LiteralPath $rollback)) { Move-Item -LiteralPath $rollback -Destination $dataRoot }
    throw
  }
  if (Test-Path -LiteralPath $rollback) { Write-Output "Restore completed. Previous data is preserved at: $rollback" }
  else { Write-Output "Restore completed to new data directory: $dataRoot" }
}
finally {
  if (Test-Path -LiteralPath $work) { Remove-Item -LiteralPath $work -Recurse -Force }
  if (Test-Path -LiteralPath $stageRoot) { Remove-Item -LiteralPath $stageRoot -Recurse -Force }
}
