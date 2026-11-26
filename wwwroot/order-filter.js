(function () {
  // Order navigation panel. The actual filtering happens inside render() in app.js
  // (activeOrder is a shared top-level binding); this file only draws the list
  // and routes clicks.
  function draw() {
    const nav = document.querySelector('#orderNav');
    if (!nav) return;
    nav.innerHTML = (window.__orders || []).map(o => `<button class="category-link ${activeOrder === o.number ? 'active' : ''}" data-order-filter="${esc(o.number)}"><span class="cat-dot order-dot"></span>${esc(o.number)}${o.parkName ? ' - ' + esc(o.parkName) : ''}<span class="cat-count">${items.filter(x => x.orderNumber === o.number).length}</span></button>`).join('');
  }
  document.addEventListener('click', event => {
    const button = event.target.closest('[data-order-filter]');
    if (!button) return;
    activeOrder = button.dataset.orderFilter;
    activeCategory = '';
    const search = document.querySelector('#search');
    if (search) search.value = '';
    render();
    if (window.matchMedia('(max-width: 720px)').matches) window.closeMobileNav?.();
  });
  window.addEventListener('maf:catalog-rendered', draw);
  draw();
})();
