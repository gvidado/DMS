'use strict';
const $ = id => document.getElementById(id);
const app = { user: null, catalog: [], definitions: new Map(), config: null, state: null, stream: null, selection: new Set(), selectionSaving: false, view: 'parameters', resetUser: null, setup: false, initializing: false, busy: false, lastEvents: '', connected: false };
const names = { parameters: ['实时参数', '关注关键参数，掌握设备当前状态。'], overview: ['接收概览', '查看报文接收、协议校验与帧计数的运行情况。'], settings: ['监听配置', '配置设备数据入口与SPP协议解析方式。'], decoders: ['解码配置', '读取Excel生成配置，手工定义SPP包并验证解码结果。'], accounts: ['账号管理', '管理内网操作员账号与各自的显示方案。'] };
function element(tag, cls, text) { const el = document.createElement(tag); if (cls) el.className = cls; if (text !== undefined) el.textContent = text; return el; }
function addExcelImportClear(id, inputId, after, onClear) {
  const button = element('button', 'button', '清空'); button.id = id; button.type = 'button';
  button.dataset.excelInput = inputId; button.title = '清空本次文件选择、未保存预览和错误提示';
  button.addEventListener('click', onClear); after.after(button); return button;
}
function setText(id, value) { const el = $(id); const s = String(value); if (el.textContent !== s) el.textContent = s; }
const numberFormatter=new Intl.NumberFormat('zh-CN');
function number(value) { return numberFormatter.format(Number(value || 0)); }
const metricCells=new Map();
const overviewPackets=new Map();
let overviewPacketOrder=null, overviewDiagnosticLabels='';
function metric(id,value,unit){let cell=metricCells.get(id);if(!cell){const text=document.createTextNode('');$(id).replaceChildren(text,element('small','',unit));cell={text,value:null};metricCells.set(id,cell);}const next=String(value)+' ';if(cell.value!==next){cell.text.nodeValue=next;cell.value=next;}}
function parameterReference(p) { const code = String(p.Code ?? p.code ?? '').trim(); return p.Reference || p.reference || (code ? /^(?:TM|TME|TMB)\./.test(code) ? code : 'TM.' + code : ''); }
function parameterValueReference(p) { return p.ValueReference || p.valueReference || parameterReference(p) + '.value'; }
function parameterSearchText(p) { return [p.Code ?? p.code, p.Name ?? p.name, parameterValueReference(p)].join(' ').toLowerCase(); }
let toastTimer;
function toast(message, error = false) { if(!error&&(app.configurationWritePending||app.configurationWriteApplied)){message+=app.configurationWritePending?'（已保存待生效，运行中每2分钟统一切换）':'（已立即生效）';app.configurationWritePending=app.configurationWriteApplied=false;}const t = $('toast'); t.textContent = message; t.className = 'toast' + (error ? ' error' : ''); clearTimeout(toastTimer); toastTimer = setTimeout(() => t.classList.add('hidden'), error ? 6500 : 3500); }
async function api(path, body) {
  path=window.ConfigurationUI?.readPath(path,body)||path;
  const options = { credentials: 'same-origin', cache: 'no-store' };
  if (body !== undefined) { options.method = 'POST'; options.headers = { 'Content-Type': 'application/json', 'X-CSRF-Token': app.user?.csrf || '' }; options.body = JSON.stringify(body); }
  const response = await fetch('/api/' + path, options);
  let data; try { data = await response.json(); } catch { throw new Error('服务器响应异常'); }
  if (!response.ok) { if (response.status === 401 && app.user) signedOut('登录已过期，请重新登录'); throw new Error(data.error || '操作失败'); }
  if(response.headers.has('X-Configuration-Pending')){app.configurationWritePending=response.headers.get('X-Configuration-Pending')==='true';app.configurationWriteApplied=response.headers.get('X-Configuration-Applied')==='true';window.ConfigurationUI?.poll();}
  return data;
}
function signedOut(message = '') {
  window.DecoderUI?.resetImport();
  window.WorkspaceUI?.closeNavigation();
  window.CaptureUI?.reset();
  window.RuntimeUI?.reset();
  window.RawSppUI?.reset();
  window.AlarmUI?.reset();
  window.UnifiedAbnormalUI?.reset();
  window.JudgmentArchiveUI?.reset();
  window.NetworkReplayUI?.reset();
  window.SendMonitorUI?.reset();
  window.CommandSendUI?.reset();
  window.SequenceUI?.reset();
  window.FlightSequenceUI?.reset();
  window.TimeDifferenceUI?.reset();
  window.ContinuousUI?.reset();
  window.NamespaceMigrationUI?.reset();
  if (app.stream) app.stream.close(); app.stream = null; app.user = null; app.connected = false;
  window.DisplaySizeUI?.reset();
  window.ParameterDisplayUI?.reset();
  window.CommandPanelUI?.reset();
  $('startup-screen').hidden=true;$('workspace').hidden=true;$('login-screen').hidden=false;
  $('startup-screen').classList.add('hidden'); $('workspace').classList.add('hidden'); $('login-screen').classList.remove('hidden'); $('login-password').value = ''; $('login-error').textContent = message;
}
function isActive(p) {
  if (p.PacketId) return p.PacketEnabled;
  const profile = app.config?.Profile || '全程';
  return p.Group === '工仪全程' || (profile === 'IMU标定' ? p.Group === '工仪IMU标定' : profile !== '全程' && (p.Group === '工仪RVD共用' || p.Group === '工仪' + profile));
}
function setConnection(connected) {
  const changed = app.connected !== connected;
  app.connected = connected;
  const c = $('connection');
  if (!changed && c.classList.contains(connected ? 'connected' : 'disconnected')) return; c.className = 'connection ' + (connected ? 'connected' : 'disconnected');
  c.replaceChildren(element('span', 'dot'), document.createTextNode(connected ? '实时连接正常' : '连接中断 · 正在重连'));
  if (!connected && app.state) renderParameters();
  window.ContinuousUI?.renderConnection();
}
async function enter(user) {
  app.user = user; app.draftConfig=null;app.lastEvents = ''; app.state = null;
  const [catalog, config] = await Promise.all([api('catalog'), api('config')]);
  app.catalog = catalog; app.definitions = new Map(catalog.map(p => [p.Id, p])); app.config = config;
  app.user.selected = await api('selection'); app.decoderRevision = null;
  await window.ParameterDisplayUI?.load();
  $('user-name').textContent = user.name; $('user-avatar').textContent = user.name.slice(0, 1);
  $('accounts-nav').classList.toggle('hidden', !user.admin);
  $('startup-screen').hidden=true;$('login-screen').hidden=true;$('workspace').hidden=false;
  $('startup-screen').classList.add('hidden'); $('login-screen').classList.add('hidden'); $('workspace').classList.remove('hidden'); $('login-password').value = '';
  refreshSelectorFilters();
  fillConfig(); window.WorkspaceUI?.reset(); navigate(window.WorkspaceUI?.initialView()||'home',true); renderParameters();
  if(app.stream)app.stream.close();app.stream=window.RealtimeUI.start(user);
}
async function initialize() {
  if(app.initializing)return;app.initializing=true;
  $('login-screen').hidden=true;$('workspace').hidden=true;$('startup-screen').hidden=false;
  $('login-screen').classList.add('hidden');$('workspace').classList.add('hidden');$('startup-screen').classList.remove('hidden');
  $('startup-message').textContent='正在载入，请稍候…';$('startup-error').classList.add('hidden');$('startup-retry').classList.add('hidden');$('startup-retry').disabled=true;
  try {
    const info = await api('auth/me');
    app.setup = info.setupRequired;
    if (app.setup) {
      $('login-title').textContent = '创建管理员'; $('login-description').textContent = info.local ? '首次使用，在服务器本机设置管理员账号。' : '请先在服务器本机浏览器完成管理员初始化。';
      $('setup-name-field').classList.remove('hidden'); $('login-name').required = true;
      $('login-submit').textContent = '创建管理员账号'; $('login-submit').disabled = !info.local; $('login-password').minLength = 8;
    }
    if (info.user) await enter(info.user);else signedOut();
  } catch (e) {
    // A confirmed 401 in enter() already opened the expired-session form.
    if($('login-screen').classList.contains('hidden')){
      if(app.stream)app.stream.close();app.stream=null;app.user=null;app.connected=false;
      $('workspace').hidden=true;$('startup-screen').hidden=false;
      $('workspace').classList.add('hidden');$('startup-screen').classList.remove('hidden');$('startup-message').textContent='页面暂时无法载入';
      $('startup-error').textContent='请检查服务器连接后重试：'+e.message;$('startup-error').classList.remove('hidden');$('startup-retry').classList.remove('hidden');
    }
  } finally {app.initializing=false;$('startup-retry').disabled=false;}
}
$('startup-retry').addEventListener('click',initialize);
$('login-form').addEventListener('submit', async event => {
  event.preventDefault(); $('login-submit').disabled = true; $('login-error').textContent = '';
  try {
    const body = { username: $('login-username').value.trim(), password: $('login-password').value, name: $('login-name').value.trim() };
    if (app.setup) { await api('auth/setup', body); app.setup = false; $('setup-name-field').classList.add('hidden'); $('login-name').required = false; $('login-title').textContent = '登录工作空间'; $('login-description').textContent = '使用你的账号访问设备实时数据。'; $('login-submit').textContent = '登录'; }
    await enter(await api('auth/login', body));
  } catch (e) { $('login-error').textContent = e.message; } finally { $('login-submit').disabled = false; }
});
$('logout').addEventListener('click', async () => { try { await api('auth/logout', {}); signedOut(); } catch (e) { toast(e.message, true); } });
function navigate(view, currentPage=false) {
  if (!Object.prototype.hasOwnProperty.call(names,view) || (view === 'accounts' && !app.user?.admin)) return false;
  if(!currentPage&&app.user&&view!==app.view&&window.WorkspaceUI)return window.WorkspaceUI.open(view);
  if(view!==app.view)window.ParameterDisplayUI?.rememberPosition(true);
  app.view = view;
  document.body.classList.toggle('capture-view', view === 'captures');
  document.body.classList.toggle('home-view', view === 'home');
  document.body.classList.toggle('data-view', ['parameters','results','overview'].includes(view));
  document.body.classList.toggle('parameters-view', view === 'parameters');
  document.body.classList.toggle('parameter-displays-view', view === 'parameter-displays');
  document.body.classList.toggle('command-panels-view', view === 'command-panels');
  document.body.classList.toggle('command-panel-config-view', view === 'command-panel-config');
  for (const key of Object.keys(names)) $('view-' + key).classList.toggle('hidden', key !== view);
  document.querySelectorAll('.nav-item').forEach(el => el.classList.toggle('active', el.dataset.view === view));
  $('page-title').textContent = names[view][0]; $('page-subtitle').textContent = names[view][1]; $('breadcrumb-current').textContent = names[view][0];
  window.WorkspaceUI?.activate(view);
  window.ParameterDisplayUI?.activate(view);
  window.CommandPanelUI?.activate(view);
  if (view === 'raw-spp') window.RawSppUI?.load();
  if (view === 'archives') window.JudgmentArchiveUI?.load();
  if (view === 'settings' && !app.settingsDirty) api('config?draft=1').then(value=>{if(app.view==='settings'&&!app.settingsDirty){app.draftConfig=value;fillConfig();}}).catch(e=>toast(e.message,true));
  if (view === 'accounts') loadAccounts();
  if (view === 'network') window.NetworkReplayUI?.load();
  if (view === 'decoders') window.DecoderUI?.load();
  if (view === 'judgments' || view === 'results') window.ContinuousUI?.load();
  if (view === 'results') window.UnifiedAbnormalUI?.load(true);
  if (view === 'captures') window.CaptureUI?.load();
  if (view === 'overview' && app.state) renderOverview();
  if (view === 'overview') { window.RuntimeUI?.refreshAbnormal(); window.AlarmUI?.refresh(); window.SendMonitorUI?.load(); }
  if (view === 'command-config') { window.SendMonitorUI?.load(); window.SendMonitorUI?.loadCatalog(); window.CommandSendUI?.loadConfig(); }
  if (view === 'command-send') window.CommandSendUI?.load();
  if (view === 'sequence-config' || view === 'sequence-run' || view === 'sequence-execute') window.SequenceUI?.load();
  window.DisplaySizeUI?.activate();
  return true;
}
document.querySelectorAll('[data-view]').forEach(el => el.addEventListener('click', () => navigate(el.dataset.view)));
function renderState() {
  const s = app.state; const active = s.mode !== 'stopped'||['running','stopping'].includes(s.sendMonitor?.state)||s.monitoringState==='stopping'; const demo = s.mode === 'demo';
  if (app.decoderRevision === null) app.decoderRevision = s.decoderRevision;
  else if (s.decoderRevision !== app.decoderRevision && !app.refreshingCatalog) refreshCatalog();
  setText('metric-status', demo ? '演示运行' : s.monitoringState==='stopping' ? '正在停止' : active ? '监听中' : '未启动');
  setText('metric-status-note', active ? (demo ? 'pcapng文件 · ' + (s.replay?.speed || 1) + '倍速' : (s.multicastGroup ? '组播 ' + s.multicastGroup + ':' + s.port : 'UDP :' + s.port) + ' · ' + s.sources.filter(x => x.online).length + '台在线') : s.replay?.state === 'completed' ? '文件回放完成，保留最后参数值' : '等待启动监听');
  metric('metric-received',number(s.received),'帧');
  setText('metric-accepted', '有效新帧 ' + number(s.accepted));
  metric('metric-lost',number(s.lost),'帧');
  setText('metric-duplicates', '重复 ' + number(s.duplicates) + ' · 重同步 ' + number(s.outOfOrder));
  metric('metric-selected',app.user.selected.length,'项');
  setText('metric-total', '布局可用 ' + s.activeParameters + ' · 总计 ' + app.catalog.length + '项');
  setText('server-clock', '服务器时间 ' + s.serverTime);
  const banner = $('mode-banner');
  if (demo) { banner.className = 'banner demo-banner'; banner.textContent = '文件演示运行中 · ' + s.replay.filename + ' · 按抓包原始时间间隔以' + (s.replay.speed || 1) + '倍速读取。参数为文件回放，未确认映射仅作演示预览。'; }
  else if (s.error) { banner.className = 'banner error-banner'; banner.textContent = s.error; }
  else if ((app.unifiedAbnormalSummary?.pending ?? s.alarmHistory?.pending ?? 0) > 0 || s.sources.some(x => x.alarm)) { banner.className = 'banner warn-banner'; banner.textContent = '有' + number(app.unifiedAbnormalSummary?.pending ?? s.alarmHistory?.pending) + '条未解除异常，请在“判读显示”的“异常判读记录”中填写原因后解除；原始记录继续保留。'; }
  else if(active && s.rawSppOnly){banner.className='banner info-banner';banner.textContent='SPP原包监测中 · 已保留 '+number(s.rawSppRetained)+'条原包，未启用遥测参数解码。请在“SPP原包监视”查看源码。';}
  else if (active && !s.mappingReady) { banner.className = 'banner warn-banner'; banner.textContent = '正在监听真实UDP。参数映射尚待确认，当前只识别报文与统计帧计数，不显示未经验证的参数值。'; }
  else if (active && s.accepted === 0) { banner.className = 'banner info-banner'; banner.textContent = 'UDP监听已开启，等待有效数据。若长期未收到，请检查设备目标端口、来源IP、广播网络与防火墙。'; }
  else if (active) { banner.className = 'banner info-banner'; banner.textContent = '真实数据监听中 · 参数按最新有效帧更新。来源白名单、协议长度与累加和校验均在服务器执行。'; }
  else { banner.className = 'banner info-banner'; banner.textContent = '当前未接收。启动监听前请配置设备实际UDP端口；再次启动会重置本次当前值与统计。'; }
  $('listen-button').textContent = active ? '停止接收' : '开始监听'; $('listen-button').className = 'button ' + (active ? 'danger' : 'primary');
  $('demo-button').disabled = app.busy; $('settings-save').disabled = app.busy;
  window.DecoderUI?.setRunning(active);
  window.RuntimeUI?.render(s);
  window.RawSppUI?.render(s);
  window.AlarmUI?.render(s);
  window.NetworkReplayUI?.render(s);
  window.SendMonitorUI?.render(s);
  window.CommandSendUI?.render(s);
  window.CommandPanelUI?.render();
  window.SequenceUI?.render(s);
  window.ContinuousUI?.renderConnection();
  setText('side-port', demo ? '文件回放' : s.port || '待配置'); setText('side-period', demo ? '按抓包时间戳' : app.config.FramePeriodMs + ' ms（仅参考）'); setText('side-profile', s.profile);
  setText('device-name', demo ? 'pcapng文件演示' : '设备 01' + (app.config.SourceIPs.length > 1 ? ' +' + (app.config.SourceIPs.length - 1) : ''));
  setText('device-address', demo ? '文件回放 · ' + (s.replay?.speed || 1) + '倍速' : app.config.SourceIPs[0]);
  const src = s.sources[0]; const hasOnline = s.sources.some(x => x.online);
  const tag = $('device-online'); tag.className = 'status-tag ' + (demo ? 'warn' : hasOnline ? 'good' : 'neutral'); tag.textContent = demo ? '演示' : active ? hasOnline ? '在线' : '等待数据' : '未接收';
  setText('side-last', src?.lastTime || '—');
  if(app.view==='parameters')renderParameters();
  else window.ParameterDisplayUI?.render();
  if (app.view === 'overview') renderOverview();
}
function statusClass(s) { return s === '有效' ? 'good' : ['解码无效', '连接中断'].includes(s) ? 'bad' : s === '过期' || s === '待核实' ? 'warn' : 'neutral'; }
function renderParameters() { window.ParameterDisplayUI?.render(); }
function renderEvents() {
  const events = app.state?.events || []; const recent = document.createDocumentFragment(); const all = document.createDocumentFragment();
  events.forEach((e, i) => {
    if (i < 4) { const item = element('div', 'event-item'); item.append(element('span', 'dot level-' + e.Level)); const text = element('div'); text.append(element('div', 'event-message', e.Message), element('div', 'event-time', e.Time.slice(11))); item.append(text); recent.append(item); }
    const item = element('div', 'all-event'); item.append(element('time', '', e.Time), element('span', 'status-tag ' + (e.Level === 'error' ? 'bad' : e.Level === 'warn' ? 'warn' : 'neutral'), e.Level === 'error' ? '异常' : e.Level === 'warn' ? '注意' : '信息'), element('span', '', e.Message)); all.append(item);
  });
  $('recent-events').replaceChildren(recent); $('all-events').replaceChildren(all);
}
function renderOverview() {
  const s=app.state;
  if(!s)return;
  const eventStamp=String(s.events[0]?.Id||'');
  if(eventStamp!==app.lastEvents){app.lastEvents=eventStamp;renderEvents();}
  const p=s.packet;
  const fields = [['接收字节', number(s.bytes) + ' B'], ['有效新帧', number(s.accepted)], ['无效报文', number(s.invalid)], ['来源过滤', number(s.filtered)], ['重复帧', number(s.duplicates)], ['疑似缺帧', number(s.lost)], ['计数重同步', number(s.outOfOrder)], ['运行时长', Math.floor(s.elapsed) + ' s'], ['收包处理等待', s.processingDelayMs==null?'—':number(s.processingDelayMs)+' ms'], ['最大处理等待', s.maxProcessingDelayMs==null?'—':number(s.maxProcessingDelayMs)+' ms'], ['最近APID', p ? p.apid + ' / 0x' + p.apid.toString(16).toUpperCase() : '—'], ['14位帧计数', p?.sequence ?? '—'], ['SPP偏移 / 长度', p ? p.sppOffset + ' / ' + p.sppLength : '—'], ['累加和校验', p?.checksum || '待接收']];
  if (s.multicastGroup) fields.unshift(['组播组 / 目的端口', s.multicastGroup + ':' + s.port], ['已加入本机网卡', (s.multicastInterfaces || []).join(', ') || '未监听']);
  const diagnostic=$('diagnostic-grid'), labels=fields.map(([label])=>label).join('\0');
  if(labels!==overviewDiagnosticLabels){
    const fragment=document.createDocumentFragment();
    for(const [label,value] of fields){
      const item=element('div','diag-item');
      item.append(element('div','',label),element('div','diag-value',value));
      fragment.append(item);
    }
    diagnostic.replaceChildren(fragment);
    overviewDiagnosticLabels=labels;
  }else{
    fields.forEach(([,value],index)=>{
      const node=diagnostic.children[index].lastElementChild,text=String(value);
      if(node.textContent!==text)node.textContent=text;
    });
  }
  const statistics=s.packetStatistics||[];
  renderPacketStatistics(statistics);
  const mode = s.packetStatisticsMode === 'demo' ? '文件演示' : '实时监听';
  setText('packet-stat-session', s.packetStatisticsMode ? '本次' + mode + ' · 启动于 ' + s.packetStatisticsStartedAt + (s.mode === 'stopped' ? ' · 已停止，保留本次统计' : ' · 正在接收') : '等待启动监听，以下按当前已启用的SPP包显示');
  setText('packet-stat-summary', statistics.length + '个已启用SPP包 · 已收 ' + number(statistics.reduce((sum, entry) => sum + entry.received, 0)) + ' · 重复 ' + number(statistics.reduce((sum, entry) => sum + entry.duplicates, 0)));
}
function renderPacketStatistics(statistics){
  const body=$('packet-stat-rows'),order=JSON.stringify(statistics.map(entry=>entry.id));
  const reordered=overviewPacketOrder!==order;
  if(reordered)overviewPackets.clear();
  for(const entry of statistics){
    const signature=JSON.stringify(entry), previous=overviewPackets.get(entry.id);
    if(previous?.signature===signature)continue;
    const node=packetStatisticRow(entry);
    if(previous)previous.node.replaceWith(node);
    overviewPackets.set(entry.id,{node,signature});
  }
  if(!reordered)return;
  overviewPacketOrder=order;
  if(statistics.length)body.replaceChildren(...[...overviewPackets.values()].map(row=>row.node));
  else{
    const row=element('tr'),cell=element('td','muted','当前没有已启用的SPP包，请在“解码配置”中启用需要接收的包。');
    cell.colSpan=12;
    row.append(cell);
    body.replaceChildren(row);
  }
}
function packetStatisticRow(entry){
    const row = element('tr'); row.dataset.packetId = entry.id;
    const identity = element('td'); identity.append(element('div', 'param-name', entry.name));
    const hex = (value, width) => '0x' + Number(value).toString(16).toUpperCase().padStart(width, '0');
    identity.append(element('div', 'muted small-text mono', (entry.outerSourceId>=0?entry.telemetryNamespace+' / 数据源 '+hex(entry.outerSourceId,4)+' · ':'')+'APID ' + entry.apid + ' · 源地址 ' + hex(entry.sourceAddress, 4) + ' · 业务 ' + hex(entry.serviceType, 2) + '/' + hex(entry.serviceSubtype, 2)));
    const status = element('td'); status.append(element('span', 'status-tag ' + (entry.decodingEnabled ? 'good' : 'warn'), entry.status));
    const received = element('td', 'mono', number(entry.received)); received.dataset.field = 'received';
    const duplicates = element('td', 'mono' + (entry.duplicates ? ' spp-duplicates' : ''), number(entry.duplicates)); duplicates.dataset.field = 'duplicates';
    const health=element('td');health.dataset.field='health';const h=entry.health || {};health.append(element('span','status-tag '+(h.active && h.timedOut?'bad':h.active && h.state==='正常'?'good':'neutral'),h.state || '未开启'));if(h.enabled)health.append(element('div','muted small-text','最近有效：'+(h.lastValid || '—')));const accepted=element('td','mono',number(entry.accepted));accepted.dataset.field='accepted';const lastSequence=element('td','mono',entry.lastSequence>=0?entry.lastSequence:'—');lastSequence.dataset.field='last-sequence';const resync=element('td','mono',number(entry.outOfOrder));resync.dataset.field='resync';if(entry.lastResyncAt){resync.title=entry.lastResyncFrom+' → '+entry.lastResyncTo+' / '+entry.lastResyncAt;resync.append(element('div','muted small-text',entry.lastResyncFrom+' → '+entry.lastResyncTo),element('div','muted small-text',entry.lastResyncAt));}row.append(identity, status, health, received, accepted, duplicates, lastSequence, resync);
    for(const time of [entry.lastMatch,entry.lastValidFrame,entry.lastParameterUpdate])row.append(element('td','mono spp-timestamp',time||'—'));
    const outcome=element('td','spp-result',entry.lastOutcome||'—');
    if(entry.lastOutcome)outcome.title=entry.lastOutcome;
    row.append(outcome);
    return row;
}
async function control(action) {
  if (app.busy) return; app.busy = true; $('listen-button').disabled = true; $('demo-button').disabled = true;
  try { await api('control', { action }); toast(action === 'start' ? 'UDP接收与发送监视已同时启动' : action === 'demo' ? '文件演示已启动，当前' + (app.state?.replay?.speed || 1) + '倍速' : action === 'ack' ? '当前告警已确认，历史记录保留' : 'UDP接收与发送监视已全部停止'); }
  catch (e) { toast(e.message, true); if (action === 'start' && !app.config.UdpPort) navigate('settings'); }
  finally { app.busy = false; $('listen-button').disabled = false; if (app.state) renderState(); }
}
$('listen-button').title='同时控制UDP接收与发送报文监视';
$('listen-button').addEventListener('click', () => {const s=app.state;control(s&&(s.mode!=='stopped'||['running','stopping'].includes(s.sendMonitor?.state)||s.monitoringState==='stopping')?'stop':'start');});
$('demo-button').addEventListener('click', () => navigate('network'));
$('ack-alarms').addEventListener('click', () => window.UnifiedAbnormalUI?.open());
async function showPacket(raw) {
  try {
    const packet = await api('packet'); const hex = raw ? packet.rawHex : packet.hex;
    $('packet-hex').textContent = hex ? hex.match(/(?:[0-9A-F]{2} ?){1,16}/g).join('\n') : '尚未收到对应报文';
    $('packet-detail').textContent = raw && packet.rawInfo ? packet.rawInfo.time + ' · ' + packet.rawInfo.length + '字节 · ' + packet.rawInfo.result : packet.info ? packet.info.time + ' · APID ' + packet.info.apid + ' · 帧计数 ' + packet.info.sequence + ' · 校验' + packet.info.checksum : '';
  } catch (e) { toast(e.message, true); }
}
$('packet-refresh').addEventListener('click', () => showPacket(true));
$('packet-valid').addEventListener('click', () => showPacket(false));

function openSelector(ids, onSave) { if (app.selectionSaving) return; app.selectionTarget = onSave; app.selection = new Set(ids || []); $('catalog-search').value = ''; $('catalog-group').value = ''; $('catalog-stream').value = ''; $('catalog-packet').value = ''; refreshSelectorFilters(); renderCatalog(); $('parameter-dialog').showModal(); }
['close-selector', 'cancel-selector'].forEach(id => $(id).addEventListener('click', () => $('parameter-dialog').close()));
function parameterStream(p) { const stream=p.TelemetryNamespace; if(stream==='TME'||stream==='TMB')return stream; const match=/^(TME|TMB)\./.exec(parameterReference(p)); return match?match[1]:'unclassified'; }
function refreshSelectorFilters() {
  const groups=[...new Set(app.catalog.map(p=>p.Group))],packets=new Map();
  for(const p of app.catalog)if(p.PacketId&&!packets.has(p.PacketId))packets.set(p.PacketId,{name:p.PacketName||p.PacketId,stream:parameterStream(p)});
  for(const [id,options]of [['catalog-group',groups.map(g=>new Option(g,g))],['catalog-packet',[...packets].map(([id,p])=>new Option(p.name+' / '+(p.stream==='unclassified'?'来源未区分':p.stream),id))]]){
    const select=$(id),previous=select.value;select.replaceChildren(new Option(id==='catalog-group'?'全部解码表':'全部SPP包',''),...options);if(options.some(o=>o.value===previous))select.value=previous;
  }
}
function filteredCatalog() { const query = $('catalog-search').value.trim().toLowerCase(), group = $('catalog-group').value, stream=$('catalog-stream').value, packet=$('catalog-packet').value; return app.catalog.filter(p => (!group || group === p.Group) && (!stream||stream===parameterStream(p)) && (!packet||packet===p.PacketId) && (!query || parameterSearchText(p).includes(query))); }
function renderCatalog() {
  const fragment = document.createDocumentFragment();
  const visible=filteredCatalog();
  for (const p of visible) {
    const label = element('label', 'catalog-option'); label.dataset.parameterId=p.Id; const checkbox = element('input'); checkbox.type = 'checkbox'; checkbox.value=p.Id; checkbox.checked = app.selection.has(p.Id);
    checkbox.addEventListener('change', () => { if (checkbox.checked) app.selection.add(p.Id); else app.selection.delete(p.Id); updateSelectionTotal(); });
    const text = element('div', 'option-label'); const line = element('div', 'option-name', p.Name); line.append(element('span', 'option-code', parameterReference(p))); line.title = parameterValueReference(p);
    text.append(line, element('div', 'option-meta', 'Byte' + p.Offset + (p.BitStart >= 0 ? ' · B' + p.BitStart + ' / ' + p.BitCount + '位' : ' · ' + p.Length + '字节') + ' · ' + (p.Supported ? p.Type : '定义待核实')));
    label.append(checkbox, text, element('span', 'option-group', (p.PacketName ? p.PacketName + ' / ' : '') + p.Group + (isActive(p) ? '' : ' · 未启用'))); fragment.append(label);
  }
  if(!visible.length)fragment.append(element('p','catalog-empty muted','没有符合当前数据流、SPP包、解码表及搜索条件的参数，请调整筛选。'));
  $('catalog-options').replaceChildren(fragment); updateSelectionTotal();
}
function updateSelectionTotal() { setText('selection-total', '已选 ' + app.selection.size + ' 项 · 当前筛选 ' + filteredCatalog().length + ' 项'); }
async function refreshCatalog() {
  app.refreshingCatalog = true;
  try {
    const [catalog, selection, config] = await Promise.all([api('catalog'), api('selection'), api('config')]);
    app.config = config;
    app.catalog = catalog; app.definitions = new Map(catalog.map(p => [p.Id, p])); app.user.selected = selection;
    refreshSelectorFilters(); if($('parameter-dialog').open)renderCatalog();
    app.decoderRevision = app.state?.decoderRevision ?? app.decoderRevision; renderParameters();
    if (app.view === 'decoders') await window.DecoderUI?.load();
  } catch (e) { toast(e.message, true); } finally { app.refreshingCatalog = false; }
}
$('catalog-search').addEventListener('input', renderCatalog); for(const id of ['catalog-group','catalog-stream','catalog-packet'])$(id).addEventListener('change',renderCatalog);
$('clear-selection').addEventListener('click', () => { app.selection.clear(); renderCatalog(); });
$('select-visible').addEventListener('click', () => { app.selection = new Set([...app.selection, ...filteredCatalog().map(p => p.Id)]); renderCatalog(); });
function saveDisplaySelection(ids) {
  if (!app.selectionTarget) return;
  app.selectionTarget(ids); app.selectionTarget = null; $('parameter-dialog').close();
}
$('save-selection').addEventListener('click', () => saveDisplaySelection([...app.selection], true));

const configFields = { BindAddress: 'cfg-bind', UdpPort: 'cfg-port', MulticastGroup: 'cfg-multicast', MulticastInterface: 'cfg-multicast-interface', FramePeriodMs: 'cfg-period', DataTimeoutMs: 'cfg-timeout', LossAlarmFrames: 'cfg-loss', ExpectedDatagramLength: 'cfg-length', SppOffset: 'cfg-spp', ParameterApid: 'cfg-param-apid', PayloadOffset: 'cfg-payload', Profile: 'cfg-profile', BitOrder: 'cfg-bitorder' };
function fillConfig() {
  const config=app.draftConfig||app.config;if (!config) return;
  app.settingsDirty=false;
  for (const [key, id] of Object.entries(configFields)) $(id).value = config[key];
  $('cfg-sources').value = config.SourceIPs.join(', '); $('cfg-apids').value = config.AllowedApids.join(', ');
  $('cfg-strict').checked = config.StrictLength; $('cfg-confirmed').checked = config.MappingConfirmed; $('cfg-lossalarm').checked = config.EnableLossAlarm; $('cfg-packet-timeout-alarm').checked = !!config.EnablePacketTimeoutAlarm;
}
$('settings-form').addEventListener('input',()=>{app.settingsDirty=true;});
$('settings-form').addEventListener('submit', async event => {
  event.preventDefault();
  try {
    const cfg = { ...(app.draftConfig||app.config) };
    for (const [key, id] of Object.entries(configFields)) cfg[key] = typeof cfg[key] === 'number' ? Number($(id).value) : $(id).value;
    cfg.SourceIPs = $('cfg-sources').value.split(/[,，\s]+/).filter(Boolean); const apids = $('cfg-apids').value.split(/[,，\s]+/).filter(Boolean);
    if (!apids.every(x => /^\d+$/.test(x))) throw new Error('APID请填写十进制整数，用英文逗号分隔');
    cfg.AllowedApids = apids.map(Number); cfg.StrictLength = $('cfg-strict').checked; cfg.MappingConfirmed = $('cfg-confirmed').checked; cfg.EnableLossAlarm = $('cfg-lossalarm').checked; cfg.EnablePacketTimeoutAlarm = $('cfg-packet-timeout-alarm').checked;
    await api('config', cfg); app.draftConfig = await api('config?draft=1'); fillConfig(); toast('配置已保存');
  } catch (e) { toast(e.message, true); }
});
async function loadAccounts() {
  try {
    const users = await api('users'); const fragment = document.createDocumentFragment();
    for (const user of users) { const item = element('div', 'account-item'); item.append(element('span', 'avatar', user.name.slice(0, 1))); const text = element('div'); text.append(element('h3', '', user.name), element('p', 'muted', user.username + (user.admin ? ' · 管理员' : ' · 操作员'))); const button = element('button', 'text-button', '重置密码'); button.addEventListener('click', () => openPassword(user.username)); item.append(text, button); fragment.append(item); }
    $('account-list').replaceChildren(fragment);
  } catch (e) { toast(e.message, true); }
}
$('account-form').addEventListener('submit', async event => { event.preventDefault(); try { await api('users', { username: $('new-username').value.trim(), name: $('new-name').value.trim(), password: $('new-password').value }); $('account-form').reset(); toast('账号已创建'); loadAccounts(); } catch (e) { toast(e.message, true); } });
function openPassword(username = null) { app.resetUser = username; $('password-title').textContent = username ? '重置密码 · ' + username : '修改我的密码'; $('old-password-field').classList.toggle('hidden', !!username); $('old-password').required = !username; $('password-form').reset(); $('password-dialog').showModal(); }
$('profile-button').addEventListener('click', () => openPassword()); $('close-password').addEventListener('click', () => $('password-dialog').close());
$('password-form').addEventListener('submit', async event => { event.preventDefault(); try { const self = !app.resetUser || app.resetUser === app.user.username; if (app.resetUser) await api('users/reset', { username: app.resetUser, password: $('changed-password').value }); else await api('auth/password', { oldPassword: $('old-password').value, password: $('changed-password').value }); $('password-dialog').close(); $('password-form').reset(); if (self) signedOut('密码已修改，请使用新密码登录'); else toast('密码已重置，该用户需重新登录'); } catch (e) { toast(e.message, true); } });
// The browser disposes EventSource on actual page unload. A download can fire
// beforeunload without leaving the page, so closing the stream there freezes the UI.
window.addEventListener('pageshow',event=>{if(event.persisted&&app.user){if(app.stream)app.stream.close();app.stream=window.RealtimeUI.start(app.user);}});
document.addEventListener('DOMContentLoaded', initialize);
