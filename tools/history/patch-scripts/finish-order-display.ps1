$p=Resolve-Path 'wwwroot/app.js';$s=[IO.File]::ReadAllText($p,[Text.Encoding]::UTF8)
$s=$s.Replace("Заказ № ${esc(x.orderNumber||'—')} · изделие №", "Заказ № ${esc(x.orderNumber||'—')}${x.parkName?` · ${esc(x.parkName)}`:''} · изделие №")
$s=$s.Replace('${x.notes?`<p class="document-notes">${esc(x.notes)}</p>`:}', '${x.notes?`<div class="document-notes"><b>Примечание</b><div>${esc(x.notes)}</div></div>`:}')
[IO.File]::WriteAllText($p,$s,[Text.UTF8Encoding]::new($false))
