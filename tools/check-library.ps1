param(
  [int]$Port = 5080
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$exe = @(
  (Join-Path $root 'bin\Debug\net10.0\MafLibrary.exe'),
  (Join-Path $root 'bin\Release\net10.0\MafLibrary.exe'),
  (Join-Path $root 'MafLibrary.exe')
) | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $exe) { throw 'MafLibrary.exe not found. Build the project first.' }

# The app checks the SQLite catalog itself (integrity, foreign keys, marker,
# computed numbers and uploaded files) without starting the web server.
$env:MAF_DATA = Join-Path $root 'data'
$checkOutput = & $exe --check-library 2>&1
$checkCode = $LASTEXITCODE
$checkOutput | ForEach-Object { Write-Output $_ }
if ($checkCode -ne 0) { throw "Library database check failed with exit code $checkCode" }

$summary = [regex]::Match(($checkOutput -join "`n"), 'items=(\d+) categories=(\d+) orders=(\d+) assets=(\d+)')
if (-not $summary.Success) { throw 'Cannot read counts from the database check output.' }
$dbItems = [int]$summary.Groups[1].Value
$dbCategories = [int]$summary.Groups[2].Value
$dbOrders = [int]$summary.Groups[3].Value

$base = "http://localhost:$Port"
$response = Invoke-RestMethod -Uri "$base/api/library" -TimeoutSec 10
if ($response.items.Count -ne $dbItems) { throw "API item count differs from database: db=$dbItems, api=$($response.items.Count)" }
if ($response.categories.Count -ne $dbCategories) { throw "API category count differs from database: db=$dbCategories, api=$($response.categories.Count)" }
if ($response.orders.Count -ne $dbOrders) { throw "API order count differs from database: db=$dbOrders, api=$($response.orders.Count)" }

foreach ($item in $response.items) {
  $expected = "$($item.orderNumber).$($item.productNumber)"
  if ($item.number -ne $expected) { throw "Item $($item.id): number $($item.number) does not match $expected" }
}

$uploadsRoot = Join-Path $root 'data\uploads'
$referencedFiles = @{}
foreach ($item in $response.items) {
  foreach ($asset in $item.assets) {
    $stored = [IO.Path]::GetFileName([string]$asset.storedName)
    $referencedFiles[$stored] = $true
    $assetPath = Join-Path $uploadsRoot $stored
    if (-not (Test-Path -LiteralPath $assetPath)) { throw "Missing referenced asset: $stored" }
    else {
      $actualSize = (Get-Item -LiteralPath $assetPath).Length
      if ($actualSize -ne [long]$asset.size) { throw "Asset size mismatch: $stored (db=$($asset.size), file=$actualSize)" }
    }
  }
}
$uploadFiles = @(Get-ChildItem -LiteralPath $uploadsRoot -File -Recurse -ErrorAction SilentlyContinue)
$orphans = @($uploadFiles | Where-Object { $_.FullName -notmatch '\\\.staging\\' -and -not $referencedFiles.ContainsKey($_.Name) })
if ($orphans.Count) { Write-Output "INFO: orphan files=$($orphans.Count); retained, not deleted" }
Write-Output "Assets: referenced=$($referencedFiles.Count), files=$($uploadFiles.Count)"

$required = @('/index.html', '/app.js', '/categories.js', '/orders.js', '/order-filter.js', '/style.css')
foreach ($path in $required) {
  $result = Invoke-WebRequest -Uri "$base$path" -UseBasicParsing -TimeoutSec 10
  if ($result.StatusCode -ne 200) { throw "$path returned HTTP $($result.StatusCode)" }
}
if ($response.items.Count -gt 0 -and $response.items[0].PSObject.Properties.Name -contains 'Type') {
  Write-Warning 'Legacy Type/AnalogGroup fields are still present in API responses.'
}
Write-Output "OK: db items=$dbItems, api items=$($response.items.Count), categories=$($response.categories.Count), orders=$($response.orders.Count), assets=$($referencedFiles.Count)"
