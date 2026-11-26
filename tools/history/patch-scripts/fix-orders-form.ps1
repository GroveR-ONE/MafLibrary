$path = Resolve-Path 'wwwroot/app.js'
$s = [IO.File]::ReadAllText($path, [Text.Encoding]::UTF8)
$pattern = '(?s)<div class="field"><label>[^<]*</label><input name="orderNumber".*?</div><div class="field"><label>[^<]*</label><input name="productNumber"'
$replacement = @'
<div class="field"><label>Номер заказа *</label><select name="orderNumber" required><option value="">Выберите заказ</option>${(window.__orders||[]).map(o=>`<option value="${esc(o.number)}" ${x.orderNumber===o.number?'selected':''}>${esc(o.number)}${o.parkName?' · '+esc(o.parkName):''}</option>`).join('')}<option value="__new">＋ Новый номер заказа</option></select><input class="new-order-input hidden" name="newOrderNumber" placeholder="Введите новый номер заказа"><input name="parkName" value="${esc(x.parkName||'')}" placeholder="Название парка"></div><div class="field"><label>Номер изделия *
'@
$rx = [regex]::new($pattern, [Text.RegularExpressions.RegexOptions]::Singleline)
if (-not $rx.IsMatch($s)) { throw 'order form fields not found' }
$s = $rx.Replace($s, $replacement.Trim(), 1)
$refreshOld = @'
async function refresh(){try{const d=await api('/api/library');items=d.items||[];
'@
$refreshNew = @'
async function refresh(){try{const d=await api('/api/library');window.__orders=d.orders||[];items=d.items||[];
'@
$s = $s.Replace($refreshOld.Trim(), $refreshNew.Trim())
$saveOld = @'
const val=n=>$(`[name="${n}"]`).value.trim(),order=val('orderNumber'),product=val('productNumber');
'@
$saveNew = @'
const val=n=>$(`[name="${n}"]`)?.value.trim()||'',orderChoice=val('orderNumber'),order=orderChoice==='__new'?val('newOrderNumber'):orderChoice,product=val('productNumber');
'@
$s = $s.Replace($saveOld.Trim(), $saveNew.Trim())
$inputOld = @'
number:`${order}.${product}`,name:val('name'),dimensions:val('dimensions'),notes:val('notes')
'@
$inputNew = @'
number:`${order}.${product}`,parkName:val('parkName'),name:val('name'),dimensions:val('dimensions'),notes:val('notes')
'@
$s = $s.Replace($inputOld.Trim(), $inputNew.Trim())
$hook = "$('#cancelEdit').onclick=closeModal;"
$addHook = "$('select[name=\"orderNumber\"]')?.addEventListener('change',e=>{const n=$('.new-order-input');n?.classList.toggle('hidden',e.target.value!=='__new');const o=(window.__orders||[]).find(x=>x.number===e.target.value);if(o)$('[name=\"parkName\"]').value=o.parkName||''});$('#cancelEdit').onclick=closeModal;"
$s = $s.Replace($hook, $addHook)
[IO.File]::WriteAllText($path, $s, [Text.UTF8Encoding]::new($false))
