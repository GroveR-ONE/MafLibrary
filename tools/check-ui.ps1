param([int]$Port = 5080)
$ErrorActionPreference='Stop'
$base="http://127.0.0.1:$Port"
Add-Type -AssemblyName System.Net.Http
$handler=[Net.Http.HttpClientHandler]::new();$handler.AllowAutoRedirect=$false
$client=[Net.Http.HttpClient]::new($handler)
function Get-Response([string]$Path){$client.GetAsync("$base$Path").GetAwaiter().GetResult()}
try {
  $root=Get-Response '/'
  if($root.StatusCode -ne 200){throw "Public root returned HTTP $([int]$root.StatusCode)"}
  $html=$root.Content.ReadAsStringAsync().GetAwaiter().GetResult()
  if($html -match 'app\.js|editButton|settingsButton|copyText|sourcePath|id="addProduct"|id="addCategory"|id="addOrder"'){throw 'Public page leaks private/editor controls or source paths.'}
  $public=Get-Response '/api/public/library'
  if($public.StatusCode -ne 200){throw "Public catalog API returned HTTP $([int]$public.StatusCode)"}
  $catalog=$public.Content.ReadAsStringAsync().GetAwaiter().GetResult()|ConvertFrom-Json
  if($catalog.items.Count -le 0){throw 'Public catalog contains no items.'}
  if($catalog.items[0].PSObject.Properties.Name -contains 'sourcePath'){throw 'Public API exposes sourcePath.'}
  foreach($uri in @('/api/library','/api/auth','/api/session/me','/api/backup.zip','/api/import-json','/files/not-a-real-file.png','/private-scripts/app.js','/app.js','/orders.js','/categories.js','/order-filter.js')){
    $r=Get-Response $uri
    if($r.StatusCode -ne [Net.HttpStatusCode]::Unauthorized){throw "Anonymous access was not denied for $uri (HTTP $([int]$r.StatusCode))."}
  }
  $private=Get-Response '/library'
  if($private.StatusCode -ne [Net.HttpStatusCode]::Found -or $private.Headers.Location -ne '/login.html'){throw 'Private library does not redirect anonymous users to login.'}
  $orders=Get-Response '/public.js';$ordersText=$orders.Content.ReadAsStringAsync().GetAwaiter().GetResult()
  if($ordersText -notmatch 'categorySearch' -or $ordersText -notmatch 'orderSearch' -or $ordersText -notmatch 'parkName'){throw 'Public category/order filters are missing.'}
  $css=Get-Response '/style.css';$cssText=$css.Content.ReadAsStringAsync().GetAwaiter().GetResult()
  if($cssText -notmatch 'overscroll-behavior:contain' -or $cssText -notmatch 'scrollbar-width:thin'){throw 'Modal scroll containment or minimalist scrollbars are missing.'}
  $pdfjs=Get-Response '/pdfjs/pdf.min.mjs'
  if($pdfjs.StatusCode -ne 200){throw 'PDF.js viewer module is unavailable.'}
  $publicScript=$ordersText
  if($publicScript -notmatch 'publicPdfCanvas' -or $publicScript -notmatch 'renderPublicPdf' -or $publicScript -notmatch 'publicPdfNext'){throw 'Public PDF viewer and page navigation are missing.'}
  Write-Output "PASS: public UI, filters, no editor/path controls; private endpoints/scripts/files reject anonymous access. Public catalog items=$($catalog.items.Count), categories=$($catalog.categories.Count), orders=$($catalog.orders.Count)."
}
finally{$client.Dispose()}
