'use strict';
window.RuntimeUI = (() => {
  $('demo-button').textContent = '⇄ 网络回放模拟';
  let speedPending = false, uploading = null, pending = false, revision = -1, nextRefresh = 0, generation = 0;
  const replayPanel = element('section', 'panel runtime-panel');
  replayPanel.innerHTML = `<div class="panel-heading"><div><h2>仅解码预览 · 1～5倍速（不检验网络）</h2><p id="replay-summary">尚未选择pcapng文件</p></div><button id="replay-choose" class="button small">选择 / 更换演示文件</button></div><div class="section-pad"><div class="replay-speed-line"><label>演示倍速<select id="replay-speed"><option value="1">1倍（原速）</option><option value="2">2倍</option><option value="3">3倍</option><option value="4">4倍</option><option value="5">5倍</option></select></label><span class="muted small-text">可在播放中切换，保持原始时间顺序。</span></div><p id="replay-progress-text" class="muted small-text">按报文时间戳从头播放一次，可停止或重新播放；仅在软件内部读取和解码。</p><progress id="replay-progress" max="100" value="0"></progress></div>`;
  $('view-overview').prepend(replayPanel);
  const decodePreview=element('button','button small','仅解码预览（不经过网络）');decodePreview.id='decode-preview';$('replay-choose').before(decodePreview);decodePreview.addEventListener('click',()=>start());
  const sourceDialog = element('dialog', 'packet-dialog'); sourceDialog.id = 'replay-dialog';
  sourceDialog.innerHTML = `<div class="dialog-heading"><div><h2>选择pcapng演示数据源</h2><p>保持抓包中各报文的时间间隔，可选1～5倍速，不发送UDP。</p></div><button id="replay-dialog-close" class="icon-button" aria-label="关闭文件选择">×</button></div><div class="section-pad"><div class="replay-speed-line"><label>演示倍速<select id="replay-dialog-speed"><option value="1">1倍（原速）</option><option value="2">2倍</option><option value="3">3倍</option><option value="4">4倍</option><option value="5">5倍</option></select></label></div><label class="file-field">Wireshark抓包文件<input id="replay-file" type="file" accept=".pcapng"></label><p class="muted small-text">也可在“文件检验”完成后点击“设为演示数据源”，复用已上传文件。演示使用已启用的SPP参数表，未确认映射的参数作为演示预览。</p><button id="replay-upload" class="button primary">读取演示文件</button> <button id="replay-upload-cancel" class="button hidden">取消上传</button><p id="replay-upload-status" class="muted small-text"></p><p id="replay-upload-error" class="form-error" role="alert"></p></div><div class="dialog-footer"><button id="replay-clear" class="text-button">清除演示数据源</button><button id="replay-start" class="button primary" disabled>开始1倍速演示</button></div>`;
  document.body.append(sourceDialog);
  const abnormalPanel = element('section', 'panel runtime-panel');
  abnormalPanel.innerHTML = `<div class="panel-heading"><div><h2>最近200条SPP诊断报文 <span id="live-abnormal-count" class="count-chip">0</span></h2><p>异常包与未匹配报文滚动保留；未匹配仅作信息，不触发告警。停止或重新启动接收后仍可查看，关闭服务器后清空。</p></div></div><div class="section-pad"><div class="import-action-line"><button id="live-abnormal-refresh" class="button small">刷新异常记录</button><a id="live-abnormal-export" href="/api/live-abnormal/export" class="button small" download="live-abnormal-spp.csv">导出最近200条CSV</a><button id="live-abnormal-clear" class="text-button">清除异常记录</button></div><p id="live-abnormal-error" class="form-error"></p><div class="table-scroll capture-table"><table class="data-table"><thead><tr><th>记录号 / 接收时间（北京时间）</th><th>来源 / 模式</th><th>SPP包 / 帧计数</th><th>异常原因</th><th>原文</th></tr></thead><tbody id="live-abnormal-rows"></tbody></table></div></div>`;
  $('view-overview').append(abnormalPanel);
  const detailDialog = element('dialog', 'packet-dialog'); detailDialog.id = 'live-abnormal-dialog';
  detailDialog.innerHTML = `<div class="dialog-heading"><h2 id="live-abnormal-title">SPP诊断原文</h2><button id="live-abnormal-close" class="icon-button" aria-label="关闭异常原文">×</button></div><div class="section-pad"><p id="live-abnormal-note" class="muted small-text"></p><label>SPP完整内容（含包头和校验）<textarea id="live-abnormal-spp" rows="8" readonly spellcheck="false"></textarea></label><label>完整UDP载荷<textarea id="live-abnormal-udp" rows="5" readonly spellcheck="false"></textarea></label></div>`;
  document.body.append(detailDialog);
  function duration(ms) { const s = Math.floor((ms || 0) / 1000); return Math.floor(s / 3600) + ':' + String(Math.floor(s / 60) % 60).padStart(2, '0') + ':' + String(s % 60).padStart(2, '0'); }
  function render(s) {
    if (!s) return;
    const r = s.replay || {}, active = s.mode !== 'stopped';
    decodePreview.disabled=active||s.networkReplay?.state==='sending';
    if (!speedPending) for (const id of ['replay-speed','replay-dialog-speed']) if ($(id).value !== String(r.speed || 1)) $(id).value = String(r.speed || 1);
    $('replay-speed').disabled = speedPending; $('replay-dialog-speed').disabled = speedPending;
    setText('replay-start', '开始' + (r.speed || 1) + '倍速演示');
    const labels = { empty:'未选择文件', ready:'已就绪', playing:'正在回放', completed:'回放完成', stopped:'已停止，可从头播放', failed:'回放失败' };
    setText('replay-summary', r.ready ? r.filename + ' · ' + (labels[r.state] || r.state) + ' · 当前' + (r.speed || 1) + '倍速' : '尚未选择pcapng文件');
    setText('replay-progress-text', r.ready ? '文件进度 ' + (r.progress || 0) + '% · 已输入UDP ' + number(r.delivered) + '帧 · 跳过非完整IPv4 UDP ' + number(r.skipped) + '帧 · 已播放 ' + duration(r.elapsedMs) + ' · 报文时间 ' + duration(r.captureElapsedMs) + (r.lagMs > 200 ? ' · 处理落后 ' + Math.round(r.lagMs) + 'ms' : '') : '按报文时间戳从头播放一次，可停止或重新播放；仅在软件内部读取和解码。');
    $('replay-progress').value = r.progress || 0; $('replay-choose').disabled = active || !!uploading;
    $('replay-start').disabled = active || !r.ready || !!uploading; $('replay-clear').disabled = active || !r.ready || !!uploading; $('replay-upload').disabled = active || !!uploading;
    setText('live-abnormal-count', number(s.liveAbnormalCount));
    if (app.view === 'overview' && revision !== s.liveAbnormalRevision && Date.now() >= nextRefresh) refreshAbnormal();
  }
  async function changeSpeed(event) {
    speedPending = true; if (app.state) render(app.state);
    try { const data = await api('replay/speed', { speed: Number(event.target.value) }); if (app.state) app.state.replay = data; } catch (e) { toast(e.message, true); }
    finally { speedPending = false; if (app.state) render(app.state); }
  }
  $('replay-speed').addEventListener('change', changeSpeed); $('replay-dialog-speed').addEventListener('change', changeSpeed);
  async function openSource() {
    $('replay-upload-error').textContent = ''; $('replay-upload-status').textContent = '';
    if (!sourceDialog.open) sourceDialog.showModal();
    try { const r = await api('replay/source'); if (app.state) { app.state.replay = r; render(app.state); } } catch (e) { $('replay-upload-error').textContent = e.message; }
  }
  async function start() {
    if (!app.state?.replay?.ready) return openSource();
    await control('demo');
  }
  $('replay-choose').addEventListener('click', openSource); $('replay-dialog-close').addEventListener('click', () => sourceDialog.close());
  $('replay-start').addEventListener('click', async () => { sourceDialog.close(); await control('demo'); });
  $('replay-clear').addEventListener('click', async () => { try { const r = await api('replay/clear', {}); app.state.replay = r; render(app.state); $('replay-upload-status').textContent = '演示源已清除'; } catch (e) { $('replay-upload-error').textContent = e.message; } });
  $('replay-upload').addEventListener('click', () => {
    const file = $('replay-file').files[0]; if (!file) { $('replay-upload-error').textContent = '请选择.pcapng文件'; return; }
    const localGeneration = generation, xhr = new XMLHttpRequest(); uploading = xhr; render(app.state);
    $('replay-upload-error').textContent = ''; $('replay-upload-status').textContent = '正在上传到服务器…'; $('replay-upload-cancel').classList.remove('hidden');
    xhr.open('POST', '/api/replay/upload?filename=' + encodeURIComponent(file.name)); xhr.setRequestHeader('Content-Type','application/octet-stream'); xhr.setRequestHeader('X-CSRF-Token',app.user.csrf);
    xhr.upload.onprogress = e => { if (localGeneration === generation) $('replay-upload-status').textContent = '已上传 ' + Math.round(e.loaded / 1024 / 1024) + 'MB' + (e.lengthComputable ? ' · ' + Math.round(e.loaded * 100 / e.total) + '%' : ''); };
    const finish = () => { uploading = null; $('replay-upload-cancel').classList.add('hidden'); if (app.state) render(app.state); };
    xhr.onload = () => { if (localGeneration !== generation) return; try { const data = JSON.parse(xhr.responseText); if (xhr.status !== 200) throw new Error(data.error || '读取失败'); app.state.replay = data; $('replay-upload-status').textContent = '演示文件已就绪，点击“开始演示”'; } catch (e) { $('replay-upload-error').textContent = e.message; } finally { finish(); } };
    xhr.onerror = () => { if (localGeneration === generation) { $('replay-upload-error').textContent = '上传失败，请检查服务器连接'; finish(); } };
    xhr.onabort = () => { if (localGeneration === generation) { $('replay-upload-status').textContent = '上传已取消'; finish(); } };
    xhr.send(file);
  });
  $('replay-upload-cancel').addEventListener('click', () => uploading?.abort());
  async function refreshAbnormal() {
    if (pending || !app.user) return; pending = true; nextRefresh = Date.now() + 350; const localGeneration = generation;
    try {
      const data = await api('live-abnormal/list'); if (localGeneration !== generation) return; revision = data.revision;
      const fragment = document.createDocumentFragment();
      for (const r of data.records) {
        const row = element('tr');
        for (const text of ['#' + r.id + '\n' + r.time, r.source + '\n' + r.mode + (r.alarm === false ? ' / 未匹配（信息）' : ' / 异常') + (r.captureFrame ? ' / 抓包#' + r.captureFrame : ''), r.packet + (r.sequence >= 0 ? ' / ' + r.sequence : ''), r.reason]) row.append(element('td', '', text));
        const cell = element('td'), button = element('button', 'text-button', '查看 #' + r.id); button.addEventListener('click', () => showAbnormal(r.id)); cell.append(button); row.append(cell); fragment.append(row);
      }
      $('live-abnormal-rows').replaceChildren(fragment); $('live-abnormal-error').textContent = '';
    } catch (e) { if (localGeneration === generation) $('live-abnormal-error').textContent = e.message; } finally { pending = false; }
  }
  async function showAbnormal(id) {
    const localGeneration = generation; try { const r = await api('live-abnormal/detail?id=' + id); if (localGeneration !== generation) return; $('live-abnormal-title').textContent = 'SPP诊断记录 #' + id; $('live-abnormal-note').textContent = (r.record.alarm === false ? '未匹配信息，不触发告警；' : '') + r.record.reason + '；' + r.note; $('live-abnormal-spp').value = r.sppHex; $('live-abnormal-udp').value = r.udpHex; detailDialog.showModal(); } catch (e) { toast(e.message,true); }
  }
  $('live-abnormal-refresh').addEventListener('click', refreshAbnormal); $('live-abnormal-close').addEventListener('click', () => detailDialog.close());
  $('live-abnormal-clear').addEventListener('click', async () => { try { await api('live-abnormal/clear', {}); revision = -1; await refreshAbnormal(); } catch (e) { toast(e.message,true); } });
  function reset() { ++generation; if (uploading) uploading.abort(); uploading = null; pending = false; revision = -1; sourceDialog.close(); detailDialog.close(); $('live-abnormal-rows').replaceChildren(); $('live-abnormal-spp').value = ''; $('live-abnormal-udp').value = ''; }
  return { render, start, reset, refreshAbnormal };
})();
