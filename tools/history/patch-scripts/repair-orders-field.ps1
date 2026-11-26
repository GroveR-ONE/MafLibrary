$p = Resolve-Path 'wwwroot/app.js'
$s = [IO.File]::ReadAllText($p, [Text.Encoding]::UTF8)
$bad = @'
<div class="field"><label>РќРѕРјРµСЂ С‚РѕРІР°СЂР° * value="${esc(x.productNumber||'')}" placeholder="3" required></div>
'@
$good = @'
<div class="field"><label>Номер изделия *</label><input name="productNumber" value="${esc(x.productNumber||'')}" placeholder="3" required></div>
'@
$rx = [regex]::new('<div class="field"><label>.*?value="\$\{esc\(x\.productNumber\|\|''''\)\}" placeholder="3" required></div>')
$s = $rx.Replace($s, $good.Trim(), 1)
$s = $s.Replace(@'
number:`${order}.${product}`,name:val('name'),dimensions:val('dimensions')
'@.Trim(), @'
number:`${order}.${product}`,parkName:val('parkName'),name:val('name'),dimensions:val('dimensions')
'@.Trim())
[IO.File]::WriteAllText($p, $s, [Text.UTF8Encoding]::new($false))
