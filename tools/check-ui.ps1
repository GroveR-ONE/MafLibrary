param(
  [int]$Port = 5080
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$library = Invoke-RestMethod "http://localhost:$Port/api/library"
if ($library.items.Count -le 0) { throw 'API returned no items' }
$app = Invoke-WebRequest "http://localhost:$Port/app.js?v=smoke" -UseBasicParsing
if ($app.StatusCode -ne 200) { throw 'app.js is unavailable' }
$html = Invoke-WebRequest "http://localhost:$Port/" -UseBasicParsing
$orders = Invoke-WebRequest "http://localhost:$Port/order-filter.js?v=smoke" -UseBasicParsing
$css = Invoke-WebRequest "http://localhost:$Port/style.css?v=smoke" -UseBasicParsing
if ($html.Content -notmatch 'id="categorySearch"' -or $html.Content -notmatch 'id="orderSearch"') { throw 'Sidebar category/order search inputs are missing' }
if ($orders.Content -notmatch 'orderSearch' -or $orders.Content -notmatch 'parkName') { throw 'Order search does not filter number and park name' }
if ($app.Content -notmatch 'categorySearch' -or $app.Content -notmatch 'modal-open') { throw 'Category search or modal scroll lock is missing' }
if ($css.Content -notmatch 'overscroll-behavior:contain' -or $css.Content -notmatch 'scrollbar-width:thin') { throw 'Scroll containment or unified scrollbar styling is missing' }
Write-Output 'PASS: category/order search controls, order-number/park-name filtering, detail scroll lock, contained modal scrolling, and quiet scrollbars are present.'
if ($app.Content -match 'filterLabel|categoryFilterButton|prompt\(|name="parkName"|type:\x27\x27|analogGroup') { Write-Warning 'Legacy markup or fields found in app.js; review before release.' }
if ($app.Content -match 'copyText') { Write-Output 'OK: copyText fallback is defined for HTTP clipboard use.' }
Write-Output "UI smoke prerequisites OK: items=$($library.items.Count), app.js=$($app.RawContentLength) bytes"
