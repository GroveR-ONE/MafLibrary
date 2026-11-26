param(
  [int]$Port = 5080
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$library = Invoke-RestMethod "http://localhost:$Port/api/library"
if ($library.items.Count -le 0) { throw 'API returned no items' }
$app = Invoke-WebRequest "http://localhost:$Port/app.js?v=smoke" -UseBasicParsing
if ($app.StatusCode -ne 200) { throw 'app.js is unavailable' }
if ($app.Content -match 'filterLabel|categoryFilterButton|prompt\(|name="parkName"|type:\x27\x27|analogGroup') { Write-Warning 'Legacy markup or fields found in app.js; review before release.' }
if ($app.Content -match 'copyText') { Write-Output 'OK: copyText fallback is defined for HTTP clipboard use.' }
Write-Output "UI smoke prerequisites OK: items=$($library.items.Count), app.js=$($app.RawContentLength) bytes"
