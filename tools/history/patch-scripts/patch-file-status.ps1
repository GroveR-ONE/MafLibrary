$path = (Resolve-Path 'wwwroot/app.js')
$s = [IO.File]::ReadAllText($path, [Text.Encoding]::UTF8)
$fields = @'
<div class="field wide"><label>Картинка-рендер ${item?'(замена текущего)':'*'}</label>${item?.assets?.find(a=>a.kind==='image')?`<div class="file-current">✓ Загружен: <b>${esc(item.assets.find(a=>a.kind==='image').name)}</b></div>`:''}<input name="renderFile" type="file" accept="image/*"><small class="file-replace-note hidden" data-for="renderFile">Новый файл заменит текущий рендер</small></div><div class="field"><label>Чертёж PDF ${item?'(замена текущего)':''}</label>${item?.assets?.find(a=>a.kind==='drawing')?`<div class="file-current">✓ Загружен: <b>${esc(item.assets.find(a=>a.kind==='drawing').name)}</b></div>`:''}<input name="drawingFile" type="file" accept="application/pdf,.pdf"><small class="file-replace-note hidden" data-for="drawingFile">Новый файл заменит текущий PDF</small></div><div class="field"><label>HTML-модель ${item?'(замена текущей)':''}</label>${item?.assets?.find(a=>a.kind==='model')?`<div class="file-current">✓ Загружен: <b>${esc(item.assets.find(a=>a.kind==='model').name)}</b></div>`:''}<input name="modelFile" type="file" accept="text/html,.html,.htm"><small class="file-replace-note hidden" data-for="modelFile">Новый файл заменит текущую 3D-модель</small></div>
'@
$pattern = @'
<div class="field wide"><label>Картинка-рендер.*?</div><div class="field"><label>Чертёж PDF.*?</div><div class="field"><label>HTML-модель.*?</div>
'@
$fieldRegex = [regex]::new($pattern.Trim(), [Text.RegularExpressions.RegexOptions]::Singleline)
$s = $fieldRegex.Replace($s, [Text.RegularExpressions.MatchEvaluator]{ param($m) $fields }, 1)
$hook = @'
$('#cancelEdit').onclick=closeModal;$('#saveItem').onclick=
'@
$replacement = @'
['renderFile','drawingFile','modelFile'].forEach(name=>{const input=$(`[name="${name}"]`),note=$(`[data-for="${name}"]`);input?.addEventListener('change',()=>note?.classList.toggle('hidden',!input.files.length))});$('#cancelEdit').onclick=closeModal;$('#saveItem').onclick=
'@
$s = $s.Replace($hook, $replacement)
[IO.File]::WriteAllText($path, $s, [Text.UTF8Encoding]::new($false))
