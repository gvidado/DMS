'use strict';
names['orbit-conversion'] = ['根数转换', '将瞬时轨道六根数和初始化历元转换为十九根数。'];

window.OrbitConversionUI = (() => {
  const form = $('orbit-conversion-form');
  const inputs = Array.from(form.querySelectorAll('input'));
  const quantities = [
    ['a', '半长轴'], ['e', '偏心率'], ['i', '轨道倾角'], ['node', '升交点赤经'],
    ['periapsis', '近地点幅角'], ['anomaly', '真近点角']
  ];
  const error = $('orbit-conversion-error');
  const result = $('orbit-conversion-result');
  const rows = $('orbit-conversion-rows');
  const copyStatus = $('orbit-copy-status');
  let values = [];

  function resetResult() {
    error.hidden = true;
    error.textContent = '';
    result.hidden = true;
    rows.replaceChildren();
    copyStatus.textContent = '';
    values = [];
    for (const input of inputs) input.removeAttribute('aria-invalid');
  }

  function displayError(message, control) {
    error.textContent = message;
    error.hidden = false;
    if (!control) return;
    control.setAttribute('aria-invalid', 'true');
    const details = control.closest('details');
    if (details) details.open = true;
    control.focus();
  }

  function numberValue(input) {
    const text = input.value.normalize('NFKC').trim().replace('−', '-');
    if (!/^[+-]?(?:\d+(?:\.\d*)?|\.\d+)(?:e[+-]?\d+)?$/i.test(text)) return NaN;
    return Number(text);
  }

  function calculate(event) {
    event.preventDefault();
    resetResult();
    const initial = {};
    for (const [key, label] of quantities) {
      const control = $('orbit-' + key);
      initial[key] = numberValue(control);
      if (!Number.isFinite(initial[key])) {
        displayError('请输入有效的' + label + '。', control);
        return;
      }
    }
    const epochControl = $('orbit-epoch');
    const epoch = window.TimeDifferenceUI.parseTimestamp(epochControl.value);
    if (epoch === null) {
      displayError('请输入有效的初始化历元。', epochControl);
      return;
    }
    const offsetControl = $('orbit-time-offset');
    const xControl = $('orbit-init-x');
    const offset = numberValue(offsetControl), x = numberValue(xControl);
    if (!Number.isFinite(offset) || !Number.isFinite(x)) {
      const validOffset = Number.isFinite(offset);
      displayError(validOffset ? '请输入有效的初始化量 x。' : '请输入有效的时间偏移。', validOffset ? xControl : offsetControl);
      return;
    }
    try {
      const fields = OrbitNineteen.compute(initial, epoch, offset, x);
      const list = document.createDocumentFragment();
      for (const [index, [key, value]] of Object.entries(fields).entries()) {
        const row = element('tr');
        const text = String(value);
        row.append(element('td', '', index + 1), element('th', 'mono', key), element('td', 'mono', text));
        row.children[1].scope = 'row';
        list.append(row);
        values.push(text);
      }
      rows.append(list);
      result.hidden = false;
    } catch (failure) {
      displayError(failure.message);
    }
  }

  async function copyValues() {
    const current = values;
    const text = current.join('\n');
    if (!text) return;
    let copied = false;
    try {
      if (navigator.clipboard) {
        await navigator.clipboard.writeText(text);
        copied = true;
      }
    } catch {}
    if (values !== current) return;
    if (!copied) {
      const input = element('textarea', 'orbit-copy-buffer');
      input.value = text;
      input.readOnly = true;
      document.body.append(input);
      input.select();
      try { copied = document.execCommand('copy'); }
      catch {}
      finally { input.remove(); $('orbit-copy').focus({preventScroll:true}); }
    }
    copyStatus.textContent = copied ? '已复制 19 项数值' : '复制失败，可从表格选取数值';
  }

  form.addEventListener('submit', calculate);
  form.addEventListener('input', resetResult);
  form.addEventListener('reset', resetResult);
  $('orbit-copy').addEventListener('click', copyValues);
  return { reset: () => form.reset() };
})();
