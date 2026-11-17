'use strict';
window.CaptureUI = (() => {
  const state = { id: '', owner: '', timer: null, data: null, upload: null, generation: 0, detailGeneration: 0 };
  const useNetwork = element('button','button hidden','设为网络回放数据源');useNetwork.id='capture-network';
  names.captures = ['文件检验', '导入实测pcapng，使用统一Fletcher-16校验检验SPP报文及遥测参数解码。'];
  const nav = element('button', 'nav-item'); nav.dataset.view = 'captures'; nav.append(element('span', 'nav-icon', '▤'), document.createTextNode('文件检验'));
  nav.addEventListener('click', () => navigate('captures')); $('accounts-nav').before(nav);
  const view = element('section', 'view hidden'); view.id = 'view-captures';
  view.innerHTML = `<section class="panel capture-panel"><div class="panel-heading"><div><h2>实测报文文件检验</h2><p>在服务器本地读取文件，使用开始检验时的配置快照；可与实时监听同时运行。</p></div><span class="status-tag neutral">离线检验 · 不发送UDP</span></div><div class="section-pad">
    <div class="upload-line"><label class="file-field">Wireshark导出的抓包<input id="capture-file" type="file" accept=".pcapng"></label><button id="capture-start" class="button primary">开始检验</button></div>
    <div class="form-grid capture-options"><label>检验的SPP包<select id="capture-packet"><option value="">全部包定义</option></select></label><label>目的UDP端口<input id="capture-port" type="number" min="0" max="65535" value="0"><small>0检验全部端口；可填写设备实际目的端口缩小范围</small></label></div>
    <label class="check-label"><input id="capture-disabled" type="checkbox" checked>包含尚未启用的包（用于核对新导入配置）</label><label class="check-label"><input id="capture-whitelist" type="checkbox">仅检验监听配置中的来源IP白名单</label>
    <p class="muted small-text">支持Ethernet/VLAN、回环或原始IP等封装中的IPv4 UDP。IPv6、IP分片及不支持的链路会单列提示。不设文件大小、总帧数和总块数上限，按块读取；大文件需要更长时间及足够的服务器磁盘空间。无需安装Wireshark或抓包驱动。</p>
    <div id="capture-error" class="form-error" role="alert"></div><div class="capture-progress-line"><strong id="capture-progress-text">尚未导入文件</strong><progress id="capture-progress" max="100" value="0"></progress></div>
    <div class="import-action-line"><button id="capture-cancel" class="button" disabled>停止检验</button><a id="capture-json" class="button hidden" download="capture-check.json">下载检验报告JSON</a><a id="capture-csv" class="button hidden" download="capture-frames.csv">导出完整逐帧CSV</a><a id="capture-abnormal" class="button hidden" download="capture-abnormal-spp.csv">导出异常SPP完整内容CSV</a><button id="capture-replay" class="button hidden">设为演示数据源</button><button id="capture-clear" class="text-button hidden">清除本次结果</button></div>
    <p id="capture-config-note" class="muted small-text"></p><p class="muted small-text">“异常”“部分参数异常”及包标识相同但源地址/业务字段未匹配的报文全部暂存，不受页面200条样本限制；CSV含SPP原始完整字节及异常原因。抓包截断时仅保留实际捕获的内容并标注。临时结果仅当前账号可访问，保留至关闭服务器程序或手动清除；关闭浏览器不删除，刷新可恢复。需要留存请先导出CSV。</p>
  </div></section>
  <section id="capture-result" class="panel hidden"><div class="panel-heading"><div><h2>检验结果</h2><p id="capture-verdict"></p></div></div><div class="section-pad"><div id="capture-summary" class="capture-summary"></div><p id="capture-note" class="muted small-text"></p>
  <details class="import-details"><summary>按包统计与异常原因</summary><div id="capture-reasons" class="import-issues"></div></details>
  <div class="capture-table-heading"><h3>报文样本（与Wireshark帧号对应）</h3><input id="capture-search" placeholder="搜索帧号、状态、包名或异常原因"></div>
  <p class="muted small-text">页面保留前200条普通记录和前200条问题记录。统计覆盖已处理的全部帧，完整明细见CSV。</p><div class="table-scroll capture-table"><table class="data-table"><thead><tr><th>帧号 / 时间（北京时间）</th><th>UDP来源 → 目的</th><th>SPP包 / 帧计数</th><th>状态 / 原因</th><th>详情</th></tr></thead><tbody id="capture-rows"></tbody></table></div>
  <div class="capture-table-heading"><h3>参数解码汇总</h3><input id="capture-parameter-search" placeholder="搜索参数、编码或包名"></div><p class="muted small-text">显示各参数最后一次检验到的新帧值、检验次数和异常次数，最多2000项。缺少预期真值时，不能据此确认工程值正确。</p>
  <div class="table-scroll capture-table"><table class="data-table"><thead><tr><th>参数 / SPP包</th><th>原始值 / 换算值</th><th>检验次数 / 异常次数</th><th>最近状态</th></tr></thead><tbody id="capture-parameters"></tbody></table></div></div></section>`;
  document.querySelector('.page-footer').before(view);
  const dialog = element('dialog', 'packet-dialog capture-dialog'); dialog.id = 'capture-dialog';
  dialog.innerHTML = `<div class="dialog-heading"><div><h2 id="capture-detail-title">报文详情</h2><p id="capture-detail-note"></p></div><button id="capture-detail-close" class="icon-button" aria-label="关闭报文详情">×</button></div><div class="capture-detail-body"><p id="capture-detail-error" class="form-error"></p><label>原始UDP载荷<textarea id="capture-detail-hex" rows="5" readonly spellcheck="false"></textarea></label><p class="muted small-text">以下为该帧的独立解码结果，最多2000项；未确认映射的结果用于诊断。</p><div class="table-scroll capture-table"><table class="data-table"><thead><tr><th>参数</th><th>原始值</th><th>换算值</th><th>状态</th></tr></thead><tbody id="capture-detail-rows"></tbody></table></div></div>`;
  document.body.append(dialog); $('capture-detail-close').addEventListener('click', () => dialog.close());
  function td(row, text, cls = '') { const cell = element('td', cls, text ?? ''); row.append(cell); return cell; }
  function reset() {
    clearTimeout(state.timer); state.timer = null; ++state.generation; ++state.detailGeneration;
    if (state.upload) state.upload.abort(); state.upload = null; state.id = ''; state.data = null; state.owner = '';
    $('capture-result').classList.add('hidden'); $('capture-error').textContent = ''; $('capture-progress-text').textContent = '尚未导入文件'; $('capture-progress').value = 0;
    $('capture-start').disabled = false; $('capture-cancel').disabled = true; $('capture-json').classList.add('hidden'); $('capture-csv').classList.add('hidden'); $('capture-replay').classList.add('hidden'); useNetwork.classList.add('hidden'); $('capture-abnormal').classList.add('hidden'); $('capture-clear').classList.add('hidden'); dialog.close();
  }
  async function load() {
    if (!app.user) return;
    if (state.owner && state.owner !== app.user.username) reset(); state.owner = app.user.username;
    try {
      const data = await api('decoders'); const old = $('capture-packet').value;
      $('capture-packet').replaceChildren(new Option('全部包定义', ''), ...data.configuration.Packets.map(p => new Option(p.Name + ' / APID ' + p.Apid + (p.Enabled ? '' : ' / 尚未启用'), p.Id)));
      if (data.configuration.Packets.some(p => p.Id === old)) $('capture-packet').value = old;
      if (!state.id && !state.upload) { const jobs = await api('captures/list'); if (jobs.length) state.id = jobs[0].id; }
      if (state.id) { clearTimeout(state.timer); await poll(state.generation); }
    } catch (e) { $('capture-error').textContent = e.message; }
  }
  function render(data) {
    state.data = data; const busy = ['queued', 'uploading', 'running'].includes(data.state), s = data.summary;
    const labels = { queued: '等待检验', running: '正在检验', completed: '检验完成', cancelled: '已停止（结果不完整）', failed: '检验未完成' };
    $('capture-start').disabled = busy; $('capture-cancel').disabled = !busy; $('capture-progress').value = data.progress;
    $('capture-progress-text').textContent = data.filename + ' · ' + (labels[data.state] || data.state) + ' · ' + data.progress + '%';
    $('capture-error').textContent = data.error || ''; $('capture-config-note').textContent = '本次配置版本：' + data.revision + '；后续修改配置请重新检验。';
    $('capture-result').classList.remove('hidden');
    $('capture-verdict').textContent = '通过 ' + s.valid + '帧 · 异常 ' + s.invalid + '帧 · 未匹配 ' + s.unmatched + '帧 · 待确认 ' + s.diagnostic + '帧 · 不支持 ' + s.unsupported + '帧。' + (data.state === 'completed' ? '请结合异常明细与已知真值复核。' : '当前为已处理部分的结果。');
    const counters = [['文件总帧数', s.frames], ['IPv4 UDP', s.udp], ['通过', s.valid], ['待确认', s.diagnostic], ['异常', s.invalid], ['未匹配', s.unmatched], ['重复帧', s.duplicates], ['计数重同步', s.outOfOrder], ['帧计数缺口', s.missing], ['非UDP', s.other], ['不支持', s.unsupported], ['筛除', s.filtered], ['无时间戳', s.noTimestamp], ['解码字段次数', s.decoded], ['参数异常次数', s.invalidParameters]];
    $('capture-summary').replaceChildren(...counters.map(([label, value]) => { const box = element('div', 'capture-stat'); box.append(element('span', 'muted small-text', label), element('strong', 'mono', number(value))); return box; }));
    $('capture-note').textContent = data.note;
    if (data.records) {
      const fragment = document.createDocumentFragment();
      for (const p of data.packets) fragment.append(element('p', '', p.Name + ' / APID ' + p.Apid + '：匹配' + p.Matched + '，通过' + p.Valid + '，异常' + p.Invalid + '，待确认' + p.Diagnostic + '，重复' + p.Duplicates + '，计数重同步' + p.OutOfOrder + '，计数缺口' + p.Missing));
      for (const reason of data.reasons) fragment.append(element('p', 'issue-warning', reason.count + '次：' + reason.reason));
      $('capture-reasons').replaceChildren(fragment); renderRows(); renderParameters();
    }
    for (const [id, format] of [['capture-json', 'json'], ['capture-csv', 'csv'], ['capture-abnormal', 'errors']]) { $(id).classList.toggle('hidden', busy); $(id).href = '/api/captures/export?id=' + data.id + '&format=' + format; }
    $('capture-abnormal').textContent = '导出异常SPP完整内容CSV（' + number(s.abnormalSaved || 0) + '条）';
    $('capture-clear').classList.toggle('hidden', busy); $('capture-replay').classList.toggle('hidden', data.state !== 'completed');useNetwork.classList.toggle('hidden',data.state!=='completed');
  }
  function renderRows() {
    const query = $('capture-search').value.trim().toLowerCase(), fragment = document.createDocumentFragment();
    for (const r of (state.data?.records || []).filter(r => [r.Frame, r.Source, r.Destination, r.PacketName, r.Status, r.Reason].join(' ').toLowerCase().includes(query)).sort((a, b) => a.Frame - b.Frame)) {
      const row = element('tr'), first = td(row, '#' + r.Frame, 'mono'); first.append(element('div', 'small-text', r.Time || '无时间戳'));
      td(row, (r.Source || '—') + ' → ' + (r.Destination || '—')); td(row, (r.PacketName || '—') + (r.Sequence >= 0 ? ' / ' + r.Sequence : ''));
      const status = td(row, r.Status); status.append(element('div', 'muted small-text', r.Reason)); const action = td(row, '');
      const button = element('button', 'text-button', '查看 #' + r.Frame); button.addEventListener('click', () => detail(r.Frame)); action.append(button); fragment.append(row);
    }
    $('capture-rows').replaceChildren(fragment);
  }
  function renderParameters() {
    const query = $('capture-parameter-search').value.trim().toLowerCase(), fragment = document.createDocumentFragment();
    for (const p of (state.data?.parameters || []).filter(p => [p.Packet, parameterSearchText(p), p.Group].join(' ').toLowerCase().includes(query))) {
      const row = element('tr'), name = td(row, p.Name + ' / ' + parameterReference(p)); name.title = parameterValueReference(p); name.append(element('div', 'muted small-text', p.Packet + ' · ' + p.Group));
      td(row, (p.Raw ?? '—') + ' / ' + (p.Value ?? '—') + (p.Unit ? ' ' + p.Unit : '')); td(row, p.Seen + ' / ' + p.Invalid, 'mono');
      const status = td(row, p.Status); if (p.Diagnostic) status.append(element('div', 'muted small-text', p.Diagnostic)); fragment.append(row);
    }
    $('capture-parameters').replaceChildren(fragment);
  }
  async function poll(generation) {
    if (generation !== state.generation || !state.id || !app.user) return;
    try {
      let data = await api('captures/status?id=' + state.id);
      if (generation !== state.generation) return;
      const busy = ['queued', 'running', 'uploading'].includes(data.state);
      if (!busy) data = await api('captures/status?id=' + state.id + '&full=true');
      if (generation !== state.generation) return; render(data);
      if (busy) state.timer = setTimeout(() => poll(generation), 800);
    } catch (e) { if (generation === state.generation) { $('capture-error').textContent = e.message; $('capture-start').disabled = false; $('capture-cancel').disabled = true; } }
  }
  async function detail(frame) {
    const generation = ++state.detailGeneration;
    $('capture-detail-title').textContent = '抓包帧 #' + frame; $('capture-detail-note').textContent = '正在读取报文详情…'; $('capture-detail-error').textContent = ''; $('capture-detail-hex').value = ''; $('capture-detail-rows').replaceChildren(); if (!dialog.open) dialog.showModal();
    try {
      const data = await api('captures/detail?id=' + state.id + '&frame=' + frame); if (generation !== state.detailGeneration) return;
      $('capture-detail-note').textContent = data.frame.Status + ' · ' + (data.frame.PacketName || '未匹配SPP包') + ' · ' + (data.frame.Time || '无时间戳');
      $('capture-detail-error').textContent = data.error; $('capture-detail-hex').value = data.hex;
      const fragment = document.createDocumentFragment(); for (const p of data.rows) { const row = element('tr'); td(row, p.name + ' / ' + parameterReference(p)); td(row, p.raw, 'mono'); td(row, (p.value ?? '') + (p.unit ? ' ' + p.unit : '')); td(row, p.status + (p.diagnostic ? '：' + p.diagnostic : '')); fragment.append(row); } $('capture-detail-rows').replaceChildren(fragment);
    } catch (e) { if (generation === state.detailGeneration) $('capture-detail-error').textContent = e.message; }
  }
  $('capture-search').addEventListener('input', renderRows); $('capture-parameter-search').addEventListener('input', renderParameters);
  $('capture-start').addEventListener('click', () => {
    const file = $('capture-file').files[0]; if (!file) { $('capture-error').textContent = '请选择Wireshark导出的.pcapng文件'; return; }
    if (!$('capture-port').checkValidity()) { $('capture-error').textContent = '目的UDP端口应为0～65535'; return; }
    clearTimeout(state.timer); ++state.generation; const generation = state.generation; state.id = ''; state.data = null; state.owner = app.user.username;
    $('capture-error').textContent = ''; $('capture-result').classList.add('hidden'); $('capture-start').disabled = true; $('capture-cancel').disabled = false; $('capture-clear').classList.add('hidden'); $('capture-json').classList.add('hidden'); $('capture-csv').classList.add('hidden'); $('capture-replay').classList.add('hidden'); useNetwork.classList.add('hidden'); $('capture-abnormal').classList.add('hidden');
    $('capture-rows').replaceChildren(); $('capture-parameters').replaceChildren(); $('capture-reasons').replaceChildren();
    const query = new URLSearchParams({ filename: file.name, port: $('capture-port').value || '0', packetId: $('capture-packet').value, includeDisabled: String($('capture-disabled').checked), whitelist: String($('capture-whitelist').checked) });
    const xhr = new XMLHttpRequest(); state.upload = xhr; xhr.open('POST', '/api/captures/start?' + query); xhr.setRequestHeader('Content-Type', 'application/octet-stream'); xhr.setRequestHeader('X-CSRF-Token', app.user.csrf);
    $('capture-progress-text').textContent = '正在上传文件到本地服务器…'; $('capture-progress').value = 0;
    xhr.upload.onprogress = event => { if (generation !== state.generation) return; $('capture-progress').value = event.lengthComputable ? event.loaded * 100 / event.total : 0; $('capture-progress-text').textContent = '正在上传到本地服务器 · ' + Math.round(event.loaded / 1024 / 1024) + 'MB'; };
    xhr.onload = () => {
      if (generation !== state.generation) return; state.upload = null;
      try { const data = JSON.parse(xhr.responseText); if (xhr.status !== 200) throw new Error(data.error || '上传失败'); state.id = data.id; poll(generation); }
      catch (e) { $('capture-error').textContent = e.message; $('capture-start').disabled = false; $('capture-cancel').disabled = true; }
    };
    xhr.onerror = () => { if (generation === state.generation) { state.upload = null; $('capture-error').textContent = '上传失败，请检查服务器连接'; $('capture-start').disabled = false; $('capture-cancel').disabled = true; } };
    xhr.send(file);
  });
  $('capture-cancel').addEventListener('click', async () => {
    if (state.upload) { state.upload.abort(); state.upload = null; ++state.generation; $('capture-progress-text').textContent = '上传已停止'; $('capture-start').disabled = false; $('capture-cancel').disabled = true; return; }
    if (!state.id) return; try { await api('captures/cancel?id=' + state.id, {}); } catch (e) { $('capture-error').textContent = e.message; }
  });
  $('capture-clear').addEventListener('click', async () => { try { if (state.id) await api('captures/delete?id=' + state.id, {}); reset(); } catch (e) { $('capture-error').textContent = e.message; } });
  $('capture-replay').before(useNetwork);
  useNetwork.addEventListener('click',async()=>{try{await api('network-replay/use-capture?id='+state.id,{});navigate('network');toast('已设为网络回放数据源，请预检后开始真实UDP发送');}catch(e){$('capture-error').textContent=e.message;}});
  $('capture-replay').textContent='设为仅解码预览数据源';
  $('capture-replay').addEventListener('click', async () => { try { await api('replay/use-capture?id=' + state.id, {}); navigate('parameters'); toast('已设为仅解码预览数据源；网络检验请使用网络回放模拟'); } catch (e) { $('capture-error').textContent = e.message; } });
  return { load, reset };
})();
