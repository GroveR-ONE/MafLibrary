$path = Resolve-Path 'wwwroot/app.js'
$s = [IO.File]::ReadAllText($path, [Text.Encoding]::UTF8)
$replacement = @'
function editor(item){
const x=item||{category:'Оборудование',orderNumber:'',parkName:'',productNumber:'',number:'',name:'',notes:'',dimensions:'',sourcePath:''};
const image=item?.assets?.find(a=>a.kind==='image'),drawing=item?.assets?.find(a=>a.kind==='drawing'),model=item?.assets?.find(a=>a.kind==='model');
const current=(a)=>a?`<div class="file-current">✓ Загружен: <b>${esc(a.name)}</b></div>`:'';
const form=`<div class="modal-content"><div class="form-grid">
<div class="field"><label>Категория *</label><input name="category" value="${esc(x.category)}" list="categories" required><datalist id="categories">${categories.map(c=>`<option>${esc(c)}</option>`).join('')}</datalist></div>
<div class="field"><label>Габариты</label><input name="dimensions" value="${esc(x.dimensions)}" placeholder="6000х2200х2600"></div>
<div class="field"><label>Номер заказа *</label><select name="orderNumber" required><option value="">Выберите заказ</option>${(window.__orders||[]).map(o=>`<option value="${esc(o.number)}" ${x.orderNumber===o.number?'selected':''}>${esc(o.number)}${o.parkName?' · '+esc(o.parkName):''}</option>`).join('')}<option value="__new">＋ Новый номер заказа</option></select><input class="new-order-input hidden" name="newOrderNumber" placeholder="Введите новый номер заказа"><input name="parkName" value="${esc(x.parkName||'')}" placeholder="Название парка"></div>
<div class="field"><label>Номер изделия *</label><input name="productNumber" value="${esc(x.productNumber||'')}" placeholder="3" required></div>
<div class="field wide"><label>Наименование *</label><input name="name" value="${esc(x.name)}" placeholder="Качель (подвес-диван)" required></div>
<div class="field wide"><label>Дополнительное описание / примечание</label><textarea name="notes" placeholder="Дополнительная информация для карточки">${esc(x.notes)}</textarea></div>
<div class="field wide"><label>Картинка-рендер ${item?'(замена текущего)':'*'}</label>${current(image)}<input name="renderFile" type="file" accept="image/*"><small class="file-replace-note hidden" data-for="renderFile">Новый файл заменит текущий рендер</small></div>
<div class="field"><label>Чертёж PDF ${item?'(замена текущего)':''}</label>${current(drawing)}<input name="drawingFile" type="file" accept="application/pdf,.pdf"><small class="file-replace-note hidden" data-for="drawingFile">Новый файл заменит текущий PDF</small></div>
<div class="field"><label>HTML-модель ${item?'(замена текущей)':''}</label>${current(model)}<input name="modelFile" type="file" accept="text/html,.html,.htm"><small class="file-replace-note hidden" data-for="modelFile">Новый файл заменит текущую 3D-модель</small></div>
<div class="field wide"><label>Исходный сетевой путь (необязательно)</label><input name="sourcePath" value="${esc(x.sourcePath)}"></div>
</div>${item?'<p style="font-size:10px;color:#888">Новые файлы заменяют текущие файлы соответствующего типа.</p>':''}</div>`;
const actions=`<footer class="modal-actions">${item?'<button class="button danger" id="deleteItem">Удалить изделие</button>':'<span></span>'}<div class="button-row"><button class="button" id="cancelEdit">Отмена</button><button class="button primary" id="saveItem">Сохранить изделие</button></div></footer>`;
modal(item?'Изменить изделие':'Добавить изделие','Заполните данные и материалы карточки.',form,actions);
$('select[name="orderNumber"]')?.addEventListener('change',e=>{const n=$('.new-order-input');n?.classList.toggle('hidden',e.target.value!=='__new');const o=(window.__orders||[]).find(z=>z.number===e.target.value);if(o)$('[name="parkName"]').value=o.parkName||''});
['renderFile','drawingFile','modelFile'].forEach(name=>{const input=$(`[name="${name}"]`),note=$(`[data-for="${name}"]`);input?.addEventListener('change',()=>note?.classList.toggle('hidden',!input.files.length))});
$('#cancelEdit').onclick=closeModal;
$('#saveItem').onclick=async()=>{const val=n=>$(`[name="${n}"]`)?.value.trim()||'',choice=val('orderNumber'),order=choice==='__new'?val('newOrderNumber'):choice,product=val('productNumber');if(!val('category')||!order||!product||!val('name'))return toast('Заполните категорию, заказ, номер изделия и наименование');if(!item&&!$('[name="renderFile"]').files.length)return toast('Добавьте картинку-рендер');const input={category:val('category'),orderNumber:order,parkName:val('parkName'),productNumber:product,number:`${order}.${product}`,name:val('name'),dimensions:val('dimensions'),notes:val('notes'),type:'',analogGroup:'',sourcePath:val('sourcePath')};try{const saved=await api(item?`/api/items/${item.id}`:'/api/items',{method:item?'PUT':'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(input)});for(const field of ['renderFile','drawingFile','modelFile']){const file=$(`[name="${field}"]`).files[0];if(file){const fd=new FormData();fd.append('file',file);await api(`/api/items/${saved.id}/files`,{method:'POST',body:fd})}}await refresh();closeModal();openDetail(saved.id);toast('Изделие сохранено')}catch(e){toast(`Не удалось сохранить: ${e.message.slice(0,100)}`)}};
if(item)$('#deleteItem').onclick=async()=>{if(!confirm('Удалить изделие и его материалы?'))return;await api(`/api/items/${item.id}`,{method:'DELETE'});closeModal();await refresh()}}
'@
$rx = [regex]::new('function editor\(item\)\{.*?\}\r?\nfunction openDetail', [Text.RegularExpressions.RegexOptions]::Singleline)
if (-not $rx.IsMatch($s)) { throw 'editor function not found' }
$s = $rx.Replace($s, $replacement.Trim() + "`r`nfunction openDetail", 1)
[IO.File]::WriteAllText($path, $s, [Text.UTF8Encoding]::new($false))
