// Order reference dictionary. The item form keeps only the order number;
// the park name is maintained here and is looked up when a card is shown.
(function(){
  const orderList=()=>window.__orders||[];
  function hideLegacyParkFields(){
    const select=document.querySelector('select[name="orderNumber"]');
    if(!select)return;
    select.querySelector('option[value="__new"]')?.remove();
    document.querySelector('[name="newOrderNumber"]')?.remove();
    document.querySelector('[name="parkName"]')?.remove();
  }
  const renderRows=()=>orderList().map(o=>`<div class="order-row"><input class="order-number-edit" data-order-number="${esc(o.number)}" value="${esc(o.number)}"><input data-order-name="${esc(o.number)}" value="${esc(o.parkName||'')}"><button class="button" data-save-order="${esc(o.number)}">Сохранить</button><button class="button danger" data-delete-order="${esc(o.number)}">Удалить</button></div>`).join('');
  window.orderSettings=function(){
    const content=`<div class="modal-content"><div class="settings-grid"><div class="setting-card wide"><h3>Заказы и парки</h3><p>Справочник заказов. В карточке изделия выбирается только номер заказа, а название парка хранится здесь.</p><div class="order-manager">${renderRows()}</div><div class="order-add"><input id="newOrderNumber" placeholder="Номер заказа"><input id="newOrderName" placeholder="Название парка"><button class="button primary" id="addOrderButton">Добавить заказ</button></div></div></div></div>`;
    modal('Заказы и парки','Справочник номеров заказов',content,`<footer class="modal-actions"><span></span><button class="button" id="settingsClose">Готово</button></footer>`);
    $('#settingsClose').onclick=closeModal;
    root.querySelectorAll('[data-save-order]').forEach(button=>button.onclick=async()=>{const number=button.dataset.saveOrder;const input=root.querySelector(`[data-order-name="${number}"]`);const numberInput=root.querySelector(`[data-order-number="${number}"]`);try{if(numberInput.value.trim()!==number)await api(`/api/orders/${encodeURIComponent(number)}/number`,{method:'PUT',headers:{'Content-Type':'application/json'},body:JSON.stringify({number:numberInput.value.trim()})});await api(`/api/orders/${encodeURIComponent(numberInput.value.trim())}`,{method:'PUT',headers:{'Content-Type':'application/json'},body:JSON.stringify({number:numberInput.value.trim(),name:input.value})});await refresh();orderSettings();toast('Заказ обновлён')}catch(e){toast(e.message||'Не удалось обновить заказ')}});
    root.querySelectorAll('[data-delete-order]').forEach(button=>button.onclick=async()=>{const number=button.dataset.deleteOrder;if(!confirm(`Удалить заказ ${number}?`))return;try{await api(`/api/orders/${encodeURIComponent(number)}`,{method:'DELETE'});window.__orders=(window.__orders||[]).filter(order=>order.number!==number);orderSettings();toast('Заказ удалён')}catch(e){toast(e.message||'Заказ используется изделиями и не удалён')}});
    $('#addOrderButton').onclick=async()=>{const number=$('#newOrderNumber').value.trim(),name=$('#newOrderName').value.trim();if(!number||!name)return toast('Введите номер и название парка');await api('/api/orders',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({number,name})});await refresh();orderSettings();toast('Заказ добавлен')};
  };
  document.querySelector('#addOrder')?.addEventListener('click',()=>editing?orderSettings():toast('Войдите в режим редактирования'));
  document.querySelector('#settingsButton').onclick=()=>editing?settings():toast('Войдите в режим редактирования');
  const oldRefresh=window.refresh;
  // Keep the park name visible in the open card, while it remains outside the item record.
  const originalOpenDetail=window.openDetail;
  if(originalOpenDetail)window.openDetail=function(id){originalOpenDetail(id);const item=items.find(x=>x.id===id),order=orderList().find(o=>o.number===item?.orderNumber);if(order){const detail=document.querySelector('.detail-name');if(detail&&!detail.textContent.includes(order.parkName))detail.textContent=detail.textContent.replace(' · изделие',` · ${order.parkName} · изделие`)}};
})();
