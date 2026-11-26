$p=Resolve-Path 'wwwroot/app.js'
$s=[IO.File]::ReadAllText($p,[Text.Encoding]::UTF8)
$idx=$s.IndexOf('const orders=')
$end=$s.IndexOf(".join('');",$idx)+9
$line=@'
const orders=(window.__orders||[]).map(o=>`<option value="${esc(o.number)}" ${x.orderNumber===o.number?'selected':''}>${esc(o.number)}${o.parkName?' - '+esc(o.parkName):''}</option>`).join('');
'@
$s=$s.Substring(0,$idx)+$line.Trim()+$s.Substring($end)
[IO.File]::WriteAllText($p,$s,[Text.UTF8Encoding]::new($false))
