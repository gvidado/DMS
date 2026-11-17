'use strict';
window.DisplaySizeUI = (() => {
  const selector = '.table-scroll, textarea[readonly], pre.hex-preview, #recent-events, #all-events, #sequence-validation';
  const prefix = 'telemetry-monitor.display-size.v1:';
  const regions = new Map();
  let scheduled = false;

  function identity(target) {
    if (target.id) return target.id;
    const tablePart = target.querySelector('tbody[id], table[id], thead[id]');
    if (tablePart) return tablePart.id;
    const owner = target.closest('.view, dialog');
    return (owner?.id || 'display') + ':' + (target.querySelector('thead')?.textContent || 'table').trim();
  }
  function storageKey(target, id) {
    if (!app.user) return '';
    const sequence = target.closest('#view-sequence-run, #view-sequence-execute');
    return prefix + encodeURIComponent(app.user.username) + ':' + id + (sequence ? ':' + sequence.id : '');
  }
  function defaultHeight(target) {
    if (target.matches('textarea, pre')) return 240;
    if (target.id === 'recent-events') return 260;
    if (target.classList.contains('short-table')) return 280;
    if (target.classList.contains('command-route-scroll')) return 240;
    if (target.closest('.spp-stat-panel')) return 320;
    if (target.closest('.parameter-panel, .judgment-display-panel') || target.classList.contains('sequence-table-scroll')) return 480;
    return 400;
  }
  function readSize(key) {
    if (!key) return null;
    try {
      const value = JSON.parse(localStorage.getItem(key));
      return value && Number.isFinite(value.width) && value.width >= 100 && value.width <= 30000 &&
        Number.isFinite(value.height) && value.height >= 120 && value.height <= 10000 ? value : null;
    } catch { return null; }
  }
  function apply(state, size) {
    const width = size ? Math.round(size.width) + 'px' : '100%';
    const height = (size ? Math.round(size.height) : state.defaultHeight) + 'px';
    state.appliedWidth = width; state.appliedHeight = height;
    state.target.style.width = width; state.target.style.height = height;
    state.controls.style.width = width;
    state.reset.disabled = !size;
  }
  function remember(state) {
    if (!app.user || !state.key || state.key !== storageKey(state.target, state.id)) return;
    // Only changes made by the resize grip or keyboard become saved preferences.
    if (state.target.style.width === state.appliedWidth && state.target.style.height === state.appliedHeight) return;
    const rect = state.target.getBoundingClientRect();
    if (rect.width <= 0 || rect.height <= 0) return;
    state.appliedWidth = state.target.style.width; state.appliedHeight = state.target.style.height;
    state.controls.style.width = state.appliedWidth; state.reset.disabled = false;
    try { localStorage.setItem(state.key, JSON.stringify({width: Math.round(rect.width), height: Math.round(rect.height)})); } catch {}
  }
  const observer = new ResizeObserver(entries => {
    for (const entry of entries) {
      const state = regions.get(entry.target);
      if (state) remember(state);
    }
  });
  function resetSize(state) {
    if (state.key) try { localStorage.removeItem(state.key); } catch {}
    apply(state, null);
  }
  function attach(target) {
    if (regions.has(target)) return;
    const id = identity(target), controls = element('div', 'display-size-controls');
    controls.dataset.displayFor = id;
    const hint = element('span', '', '拖动右下角调整大小'), reset = element('button', 'text-button', '恢复默认');
    reset.type = 'button'; reset.title = '恢复此显示区域的默认尺寸';
    controls.append(hint, reset); target.after(controls);
    const state = {target, id, controls, reset, key: '', defaultHeight: defaultHeight(target)};
    regions.set(target, state); target.classList.add('resizable-display');
    if (!target.hasAttribute('tabindex')) target.tabIndex = 0;
    target.setAttribute('aria-description', '显示区域大小固定，不随记录数量改变。可拖动右下角或按Alt加方向键调整，Alt加Home恢复默认。');
    reset.addEventListener('click', () => resetSize(state));
    target.addEventListener('keydown', event => {
      if (!event.altKey || event.ctrlKey || event.metaKey || !['ArrowLeft','ArrowRight','ArrowUp','ArrowDown','Home'].includes(event.key)) return;
      event.preventDefault();
      if (event.key === 'Home') { resetSize(state); return; }
      const rect = target.getBoundingClientRect(), parentWidth = target.parentElement.clientWidth;
      const width = Math.min(parentWidth, Math.max(Math.min(220, parentWidth), rect.width + (event.key === 'ArrowRight' ? 20 : event.key === 'ArrowLeft' ? -20 : 0)));
      const height = Math.max(120, rect.height + (event.key === 'ArrowDown' ? 20 : event.key === 'ArrowUp' ? -20 : 0));
      target.style.width = width + 'px'; target.style.height = height + 'px'; remember(state);
    });
    apply(state, null); observer.observe(target);
  }
  function activate() {
    document.querySelectorAll(selector).forEach(attach);
    for (const [target, state] of regions) {
      if (!target.isConnected) { observer.unobserve(target); state.controls.remove(); regions.delete(target); continue; }
      if (target.nextElementSibling !== state.controls) target.after(state.controls);
      const key = storageKey(target, state.id);
      if (key !== state.key) { state.key = key; apply(state, readSize(key)); }
    }
  }
  function schedule() {
    if (scheduled) return; scheduled = true;
    requestAnimationFrame(() => { scheduled = false; activate(); });
  }
  new MutationObserver(records => {
    if (records.some(record => [...record.addedNodes, ...record.removedNodes].some(node =>
      node.nodeType === 1 && (node.matches(selector) || node.querySelector(selector))))) schedule();
  }).observe(document.body, {childList: true, subtree: true});
  window.addEventListener('storage', event => {
    for (const state of regions.values()) if (event.key === state.key || event.key === null) apply(state, readSize(state.key));
  });
  function reset() { for (const state of regions.values()) { state.key = ''; apply(state, null); } }
  activate();
  return {activate, reset};
})();
