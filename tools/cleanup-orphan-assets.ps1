param(
  [int]$Port = 5080,
  [switch]$Delete,
  [switch]$Confirm
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$uploadsRoot = Join-Path $root 'data\uploads'
$exeCandidates = @(
  (Join-Path $root 'bin\Release\net10.0\MafLibrary.exe'),
  (Join-Path $root 'bin\Debug\net10.0\MafLibrary.exe'),
  (Join-Path $root 'MafLibrary.exe')
)
$exe = $exeCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $exe) { throw 'MafLibrary.exe not found; build the project first.' }

$oldData = $env:MAF_DATA
try {
  $env:MAF_DATA = Join-Path $root 'data'
  $check = & $exe --check-library 2>&1
  $code = $LASTEXITCODE
  $check | ForEach-Object { Write-Output $_ }
  if ($code -ne 0) { throw 'Library check failed; refusing to clean any files.' }
  $summary = [regex]::Match(($check -join "`n"), 'items=(\d+) categories=(\d+) orders=(\d+) assets=(\d+)')
  if (-not $summary.Success) { throw 'Could not read database counts; refusing to clean any files.' }
  $dbItems = [int]$summary.Groups[1].Value
  $dbAssets = [int]$summary.Groups[4].Value
}
finally { $env:MAF_DATA = $oldData }

$api = Invoke-RestMethod -Uri "http://127.0.0.1:$Port/api/library" -TimeoutSec 10
  $apiAssetSum = ($api.items | ForEach-Object { $_.assets.Count } | Measure-Object -Sum).Sum
  $apiAssets = if ($null -eq $apiAssetSum) { 0 } else { [int]$apiAssetSum }
if ($api.items.Count -ne $dbItems -or $apiAssets -ne $dbAssets) {
  throw "API/database mismatch (items $($api.items.Count)/$dbItems, assets $apiAssets/$dbAssets); refusing to clean files."
}
$referenced = @{}
foreach ($item in $api.items) {
  foreach ($asset in $item.assets) { $referenced[[IO.Path]::GetFileName([string]$asset.storedName)] = $true }
}
$orphans = @(Get-ChildItem -LiteralPath $uploadsRoot -File -Recurse | Where-Object {
  $_.FullName -notmatch '\\\.staging\\' -and -not $referenced.ContainsKey($_.Name)
})
if ($orphans.Count -eq 0) { Write-Output 'No orphan assets found.'; exit 0 }

Write-Output "Orphan files (not referenced by SQLite): $($orphans.Count)"
$orphans | Select-Object FullName,Length,LastWriteTime | Format-Table -AutoSize
if (-not $Delete) { Write-Output 'Dry run only. Add -Delete to remove these files.'; exit 0 }
if (-not $Confirm) {
  $answer = Read-Host "Type DELETE $($orphans.Count) to permanently remove these files"
  if ($answer -cne "DELETE $($orphans.Count)") { Write-Output 'Cancelled; no files deleted.'; exit 0 }
}

$server = Get-CimInstance Win32_Process -Filter "name='MafLibrary.exe' or name='dotnet.exe'" -ErrorAction SilentlyContinue |
  Where-Object { $_.CommandLine -match 'MafLibrary\.exe' -or ($_.CommandLine -match 'dotnet\.exe' -and $_.CommandLine -match 'MafLibrary(\.dll|\.csproj)|dotnet run --no-launch-profile') }
if ($server) { throw 'Stop the library server before orphan deletion so an upload or JSON import cannot race cleanup.' }

foreach ($file in $orphans) { Remove-Item -LiteralPath $file.FullName -Force }
Write-Output "Deleted $($orphans.Count) orphan files."
