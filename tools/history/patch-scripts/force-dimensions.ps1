$p=Resolve-Path 'wwwroot/app.js'
$s=[IO.File]::ReadAllText($p,[Text.Encoding]::UTF8)
$i=$s.IndexOf('<div class="detail-name">')
$j=$s.IndexOf('</div></div><div class="document-actions">',$i)
if($i -lt 0 -or $j -lt 0){throw 'fragment not found'}
$prefix=$s.Substring(0,$i)
$suffix=$s.Substring($j+6)
$new=@'
<div class="detail-name">Заказ № ${esc(x.orderNumber||'—')} · изделие № ${esc(x.productNumber||'—')}</div><div class="dimensions-block"><span>Габариты</span><b>${esc(x.dimensions||'—')} мм</b></div></div><div class="document-actions">
'@
$s=$prefix+$new.Trim()+$suffix
[IO.File]::WriteAllText($p,$s,[Text.UTF8Encoding]::new($false))
