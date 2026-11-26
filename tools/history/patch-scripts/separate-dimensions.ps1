$p=Resolve-Path 'wwwroot/app.js'
$s=[IO.File]::ReadAllText($p,[Text.Encoding]::UTF8)
$old=@'
<div class="detail-name">Заказ № ${esc(x.orderNumber||'—')} · изделие № ${esc(x.productNumber||'—')} <span class="document-size">${esc(x.dimensions||'')}</span></div>
'@
$new=@'
<div class="detail-name">Заказ № ${esc(x.orderNumber||'—')} · изделие № ${esc(x.productNumber||'—')}</div><div class="dimensions-block"><span>Габариты</span><b>${esc(x.dimensions||'—')} мм</b></div>
'@
if(-not $s.Contains($old.Trim())){throw 'dimensions fragment not found'}
$s=$s.Replace($old.Trim(),$new.Trim())
[IO.File]::WriteAllText($p,$s,[Text.UTF8Encoding]::new($false))
