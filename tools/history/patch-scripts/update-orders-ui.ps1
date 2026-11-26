$path = Resolve-Path 'wwwroot/app.js'
$s = [IO.File]::ReadAllText($path, [Text.Encoding]::UTF8)
$oldOrder = @'
<div class="field"><label>Номер заказа *</label><input name="orderNumber" value="${esc(x.orderNumber||'')}" placeholder="142" required></div>
'@
$newOrder = @'
<div class="field"><label>Номер заказа *</label><select name="orderNumber" required><option value="">Выберите заказ</option>${(window.__orders||[]).map(o=>`<option value="${esc(o.number)}" ${x.orderNumber===o.number?'selected':''}>${esc(o.number)}${o.parkName?' · '+esc(o.parkName):''}</option>`).join('')}<option value="__new">＋ Новый номер заказа</option></select><input class="new-order-input hidden" name="newOrderNumber" placeholder="Введите новый номер заказа"><input name="parkName" value="${esc(x.parkName||'')}" placeholder="Название парка"></div>
'@
if (-not $s.Contains($oldOrder.Trim())) { throw 'order field not found' }
$s = $s.Replace($oldOrder.Trim(), $newOrder.Trim())
$oldFiles = @'
<div class="field wide"><label>\u041a\u0430\u0440\u0442\u0438\u043d\u043a\u0430
'@
$newFiles = @'
<div class="field wide"><label>Дополнительное описание / примечание</label><textarea name="notes" placeholder="Дополнительная информация для карточки">${esc(x.notes)}</textarea></div><div class="field wide"><label>\u041a\u0430\u0440\u0442\u0438\u043d\u043a\u0430
'@
if ($s.Contains($oldFiles.Trim())) { $s = $s.Replace($oldFiles.Trim(), $newFiles.Trim()) }
$s = $s.Replace("const val=n=>$(`[name=\"${n}\"]`).value.trim(),order=val('orderNumber'),product=val('productNumber');", "const val=n=>$(`[name=\"${n}\"]`)?.value.trim()||'',orderSelect=val('orderNumber'),order=orderSelect==='__new'?val('newOrderNumber'):orderSelect,product=val('productNumber');")
$s = $s.Replace("number:`${order}.${product}`,name:val('name'),dimensions:val('dimensions')", "number:`${order}.${product}`,parkName:val('parkName'),name:val('name'),dimensions:val('dimensions')")
$s = $s.Replace("async function refresh(){try{const d=await api('/api/library');items=d.items||[];", "async function refresh(){try{const d=await api('/api/library');window.__orders=d.orders||[];items=d.items||[];")
$s = $s.Replace("Заказ № ${esc(x.orderNumber||'—')} · изделие №", "Заказ № ${esc(x.orderNumber||'—')}${x.parkName?` · ${esc(x.parkName)}`:''} · изделие №")
$s = $s.Replace('${x.notes?`<p class="document-notes">${esc(x.notes)}</p>`:}', '${x.notes?`<div class="document-notes"><b>Примечание</b><div>${esc(x.notes)}</div></div>`:}')
$hook = "$('#cancelEdit').onclick=closeModal;$('#saveItem').onclick="
$hookAdd = "$('select[name=\"orderNumber\"]')?.addEventListener('change',e=>{const n=$('.new-order-input');n?.classList.toggle('hidden',e.target.value!=='__new');const o=(window.__orders||[]).find(x=>x.number===e.target.value);if(o)$('[name=\"parkName\"]').value=o.parkName||''});$('#cancelEdit').onclick=closeModal;$('#saveItem').onclick="
$s = $s.Replace($hook, $hookAdd)
[IO.File]::WriteAllText($path, $s, [Text.UTF8Encoding]::new($false))
