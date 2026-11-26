$p=Resolve-Path 'wwwroot/app.js';$s=[IO.File]::ReadAllText($p,[Text.Encoding]::UTF8)
$s=$s.Replace("let items=[],categories=[],activeCategory='',editing=false", "let items=[],categories=[],activeCategory='',activeOrder='',editing=false")
$s=$s.Replace("const nav=$('#categoryNav');nav.innerHTML=categories.map(c=>", "const nav=$('#categoryNav');nav.innerHTML=categories.map(c=>")
$needle="$('#categoryNav').innerHTML=categories.map(c=>`<button class=\"category-link ${activeCategory===c?'active':''}\" data-category=\"${esc(c)}\"><span class=\"cat-dot\"></span>${esc(c)}<span class=\"cat-count\">${items.filter(x=>x.category===c).length}</span></button>`).join('');"
$insert=$needle+"const orderNav=$('#orderNav');if(orderNav)orderNav.innerHTML=(window.__orders||[]).map(o=>`<button class=\"category-link ${activeOrder===o.number?'active':''}\" data-order=\"${esc(o.number)}\"><span class=\"cat-dot order-dot\"></span>${esc(o.number)}${o.parkName?' - '+esc(o.parkName):''}<span class=\"cat-count\">${items.filter(x=>x.orderNumber===o.number).length}</span></button>`).join('');"
if(-not $s.Contains($needle)){throw 'category render not found'};$s=$s.Replace($needle,$insert)
$s=$s.Replace("(!activeCategory||x.category===activeCategory)&&(!q||", "(!activeCategory||x.category===activeCategory)&&(!activeOrder||x.orderNumber===activeOrder)&&(!q||")
$s=$s.Replace("$('#allNav').onclick=()=>{activeCategory='';render()};", "$('#allNav').onclick=()=>{activeCategory='';activeOrder='';render()};")
$s=$s.Replace("if(b){activeCategory=b.dataset.category;render()}", "if(b){activeCategory=b.dataset.category;activeOrder='';render()}")
$hook="$('#categoryNav').onclick=e=>{"
$s=$s.Replace($hook,"$('#orderNav').onclick=e=>{const b=e.target.closest('[data-order]');if(b){activeOrder=b.dataset.order;activeCategory='';render()}};$hook")
[IO.File]::WriteAllText($p,$s,[Text.UTF8Encoding]::new($false))
