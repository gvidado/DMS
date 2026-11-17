'use strict';
window.ContinuousUI = (() => {
  names.judgments = ['判读配置', '管理常态化规则、Excel批量导入和TC指令代号绑定。'];
  names.results = ['判读显示', '实时查看已有判读规则对应参数的通过情况。'];
  const nav = element('button', 'nav-item'); nav.dataset.view = 'judgments'; nav.append(element('span', 'nav-icon', '≡'), document.createTextNode('常态化判读')); nav.addEventListener('click', () => navigate('judgments')); $('accounts-nav').before(nav);
  const view = element('section', 'hidden'); view.id = 'view-judgments';
  view.innerHTML = `<section class="panel"><div class="panel-heading"><div><h2>常态化判读规则</h2><p>先启动参数监听，再单独启动判读。规则启用状态与判读运行状态分别管理。</p></div><button id="rule-new" class="button primary">手动新增规则</button></div><div class="section-pad"><div class="rule-toolbar"><button id="rule-start" class="button primary">启动判读</button><button id="rule-stop" class="button">停止判读</button><span id="rule-run-status" class="status-tag neutral">判读未运行</span><span id="rule-run-detail" class="muted small-text"></span></div><div id="rule-summary" class="import-summary"></div><div class="rule-toolbar"><input id="rule-search" placeholder="搜索参数、TM引用或规则"><select id="rule-level-filter" aria-label="筛选判读级别"><option value="">全部级别</option><option>一般</option><option>重要</option><option>关键</option></select><select id="rule-enabled-filter" aria-label="筛选启用状态"><option value="">全部规则</option><option value="enabled">已启用</option><option value="disabled">已停用</option></select><button id="rule-refresh" class="button">刷新</button><a id="rule-export-csv" class="button" href="/api/continuous-rules/export.csv" download="continuous-rules.csv">导出规则CSV</a><a class="button" href="/api/continuous-rules/export" download="continuous-rules.json">导出规则JSON</a></div><p class="muted small-text">SPP填写数据源并关联解码表保存后自动初始化：一般、不判读、完整三域模板（this.state=0，RET=Normal）；已有自定义规则保留。判读级别表示参数重要程度。配置保存后在“判读显示”查看实时结果；停用不会删除规则及已有告警记录。</p><p id="rule-error" class="form-error" role="alert"></p><div class="table-scroll"><table class="data-table"><thead><tr><th>参数 / 引用</th><th>判读级别</th><th>是否判读</th><th>配置检查</th><th>操作</th></tr></thead><tbody id="rule-rows"></tbody></table></div><div class="current-table-pagination"><span id="rule-page-note"></span><div><button id="rule-previous" class="button small">上一页</button> <button id="rule-next" class="button small">下一页</button></div></div></div></section>
  <section class="panel"><div class="panel-heading"><div><h2>从Excel批量导入</h2><p>按六列表头读取各Sheet；序号、参数代号、参数名称、判读级别、是否判读、判读规则。</p></div></div><div class="section-pad"><div class="rule-toolbar"><label class="file-field">选择规则Excel<input type="file" id="rule-file" accept=".xlsx"></label><select id="rule-import-source" aria-label="导入规则来源IP"><option value="">自动匹配来源IP</option></select><button id="rule-read" class="button primary">读取并预览</button></div><p class="muted small-text">不执行Excel宏或公式。读取预览不修改已有规则；只导入检查通过的规则，错误行完整列出并可导出CSV。重要程度：一般、重要、关键；是否判读：是／否。</p><p id="rule-import-error" class="form-error" role="alert"></p><div id="rule-preview" class="hidden"><div id="rule-import-summary" class="import-summary"></div><div class="rule-toolbar"><label>导入后的启用方式 <select id="rule-import-policy"><option value="excel">按Excel“是否判读”启用</option><option value="disabled">本次导入规则全部停用</option></select></label><button id="rule-add" class="button primary">添加到已有规则</button><button id="rule-replace" class="button">替换全部常态化规则</button></div><p class="muted small-text">添加保留已有规则，相同内容跳过，冲突会阻止添加；替换仅用本次合格规则更新全部常态化规则。</p><div id="rule-import-conflicts" class="import-issues"></div><details open class="import-details"><summary>可导入规则</summary><div class="table-scroll short-table"><table class="data-table"><thead><tr><th>来源行</th><th>参数 / 绑定</th><th>级别</th><th>是否判读</th></tr></thead><tbody id="rule-preview-rows"></tbody></table></div><p id="rule-preview-note" class="muted small-text"></p></details><details id="rule-errors-details" class="import-details"><summary id="rule-errors-summary">错误行</summary><a id="rule-errors-export" class="button small">导出全部错误CSV</a><div class="table-scroll short-table"><table class="data-table"><thead><tr><th>工作表 / 行</th><th>参数代号</th><th>错误原因</th></tr></thead><tbody id="rule-error-rows"></tbody></table></div><div class="current-table-pagination"><span id="rule-error-page-note"></span><div><button id="rule-error-previous" class="button small">上一页</button> <button id="rule-error-next" class="button small">下一页</button></div></div></details></div></div></section>`;
  $('view-decoders').after(view);
  const display=element('section','view hidden');display.id='view-results';
  display.innerHTML=`<section class="panel judgment-display-panel"><div class="panel-heading"><div><h2>参数判读监视 <span id="judgment-count" class="count-chip">0</span></h2><p>展示全部已配置规则的参数，结果自动更新</p></div><button id="judgment-open-config" class="button small">打开判读配置</button></div><div id="judgment-controls" class="section-pad"></div><div id="judgment-summary" class="judgment-summary"></div><div class="table-toolbar judgment-toolbar"><div class="search-field"><span>⌕</span><input id="judgment-search" placeholder="搜索参数代号、名称或TM引用" aria-label="搜索判读参数"></div><select id="judgment-level-filter" aria-label="判读重要程度筛选"><option value="">全部重要程度</option><option>一般</option><option>重要</option><option>关键</option></select><select id="judgment-enabled-filter" aria-label="判读是否开启筛选"><option value="">全部启用状态</option><option value="enabled">已开启</option><option value="disabled">未开启</option></select><select id="judgment-status-filter" aria-label="判读是否通过筛选"><option value="">全部判读状态</option><option value="passed">通过</option><option value="failed">未通过</option><option value="waiting">等待数据</option><option value="stopped">判读未运行</option><option value="disabled">已停用</option><option value="config-error">配置错误</option><option value="runtime-error">执行异常</option><option value="disconnected">连接中断</option><option value="unavailable">获取失败</option><option value="pending">尚未判读</option></select><button id="judgment-refresh" class="button small">刷新</button></div><p id="judgment-error" class="form-error section-inline" role="alert"></p><div class="table-scroll"><table class="parameter-table judgment-table"><thead><tr><th>参数代号</th><th>参数名称</th><th>重要程度</th><th>是否开启</th><th>工程值</th><th>判读状态 / 是否通过</th><th>更新时间</th></tr></thead><tbody id="judgment-rows"></tbody></table></div><div class="table-footer"><span id="judgment-table-summary"></span><span><span class="dot teal-dot"></span> 自动更新 · 仅Normal视为通过</span></div><div class="current-table-pagination"><span id="judgment-page-note"></span><div><button id="judgment-previous" class="button small">上一页</button> <button id="judgment-next" class="button small">下一页</button></div></div></section>`;
  view.after(display);
  const globalPanel=element('section','panel');globalPanel.id='shared-variable-panel';
  globalPanel.innerHTML=`<div class="panel-heading"><div><h2>共享变量 <span id="global-count" class="count-chip">0</span></h2><p>自动汇总规则中的gz变量。监听启动后可修改当前状态，判读运行时也可操作。</p></div><button id="global-refresh" class="button small">刷新变量</button></div><div class="section-pad"><p id="global-mode" class="muted small-text"></p><p class="muted small-text">支持数值、true/false和NIL；人工修改与规则赋值按最后一次生效。未启动判读时设置的值用于下一次启动；新监听或重启软件会清空。</p><p id="global-error" class="form-error" role="alert"></p><div class="table-scroll"><table class="data-table"><thead><tr><th>共享变量</th><th>当前值 / 类型</th><th>写入规则</th><th>读取规则</th><th>最近更新</th><th>操作</th></tr></thead><tbody id="global-rows"></tbody></table></div></div>`;view.prepend(globalPanel);
  const globalDialog=element('dialog','packet-dialog');globalDialog.id='global-dialog';globalDialog.innerHTML=`<div class="dialog-heading"><h2>修改共享变量</h2><button id="global-close" class="icon-button" aria-label="关闭共享变量编辑">×</button></div><form id="global-form" class="section-pad"><p id="global-edit-name" class="mono"></p><p id="global-old-value" class="muted small-text"></p><div class="rule-form-grid"><label>数值类型<select id="global-kind"><option value="number">数值</option><option value="boolean">布尔（true / false）</option><option value="result">判读状态</option><option value="nil">空值（NIL）</option></select></label><label>新值<input id="global-value" maxlength="128" autocomplete="off" placeholder="例如0、1或2.5"></label></div><p class="muted small-text">保存后供所有用户和判读规则共享。规则后续再次赋值时，可覆盖本次人工值。</p><p id="global-editor-error" class="form-error" role="alert"></p><div class="import-action-line"><button id="global-cancel" type="button" class="button">取消</button><button id="global-save" type="submit" class="button primary">保存变量状态</button></div></form>`;document.body.append(globalDialog);
  $('judgment-controls').append($('rule-start').closest('.rule-toolbar'));
  const controlHint=element('p','muted small-text','判读启停、工作状态和实时结果请在“数据显示 → 判读显示”查看。');$('rule-summary').before(controlHint);
  $('judgment-open-config').addEventListener('click',()=>navigate('judgments'));
  const dialog = element('dialog', 'packet-dialog rule-dialog'); dialog.id = 'rule-dialog';
  dialog.innerHTML = `<div class="dialog-heading"><h2 id="rule-dialog-title">新增常态化规则</h2><button id="rule-editor-close" class="icon-button" aria-label="关闭规则编辑">×</button></div><form id="rule-form" class="section-pad"><div class="rule-form-grid"><label>选择解码参数<select id="rule-parameter" required></select></label><label>参数名称<input id="rule-name" required maxlength="240"></label><label>判读级别<select id="rule-level"><option>一般</option><option>重要</option><option>关键</option></select></label><label>来源策略（可不选IP）<select id="rule-source"></select></label></div><p id="rule-reference" class="mono small-text"></p><p id="rule-source-note" class="muted small-text"></p><label class="checkbox-line"><input id="rule-enabled" type="checkbox">开启该参数判读</label><div class="rule-toolbar"><button type="button" id="rule-template-if" class="button small">填入IF模板</button><button type="button" id="rule-template-range" class="button small">填入范围模板</button><button type="button" id="rule-template-enum" class="button small">填入注释项模板</button></div><label class="rule-script-label">判读规则<textarea id="rule-script" rows="12" maxlength="20000" placeholder="无；填写有效规则后再开启判读" spellcheck="false"></textarea></label><p class="muted small-text">Tab缩进4个空格，Shift+Tab取消一级缩进，支持多行。支持TME/TMB引用、this.value、三域、条件和数学公式、INENUM、历史值、TC事件、AFTER/HOLD/FRAMES、时间量及gz全局变量。计时支持s/ms，位运算和.raw不开放。旧Filter需明确改写。</p><label class="rule-script-label">描述（可选）<textarea id="rule-description" rows="2" maxlength="4000"></textarea></label><p id="rule-editor-error" class="form-error" role="alert"></p><div class="import-action-line"><button type="button" id="rule-editor-cancel" class="button">取消</button><button type="submit" id="rule-save" class="button primary">检查并保存</button></div></form>`;
  document.body.append(dialog);
  const bindingPanel=element('details','import-details');bindingPanel.open=true;bindingPanel.innerHTML=`<summary>附加参数绑定</summary><p class="muted small-text">脚本中的TM引用自动列出。选择明确参数及来源；未指定来源时沿用主参数来源或SPP的固定来源。this和主参数的TM引用保持一致。</p><div id="rule-binding-fields"></div>`;$('rule-script').closest('label').before(bindingPanel);
  const tcPanel=element('section','panel');tcPanel.innerHTML=`<div class="panel-heading"><div><h2>TC指令代号绑定</h2><p>从已采集的不同指令中手动填写代号，后续规则使用TC.代号。空白保存可解除手动绑定；BC/PSS/KC自动编号可直接用于TC引用。</p></div><button id="tc-refresh" class="button">刷新指令库</button></div><div class="section-pad"><p id="tc-summary" class="muted small-text"></p><p id="tc-error" class="form-error" role="alert"></p><div class="rule-toolbar"><input id="tc-search" placeholder="搜索指令名、记录号或目的IP"><button id="tc-search-button" class="button">搜索</button></div><div class="table-scroll"><table class="data-table"><thead><tr><th>指令记录号 / 名称</th><th>目的IP / 载荷长度</th><th>TC代号</th><th>操作</th></tr></thead><tbody id="tc-rows"></tbody></table></div><div class="current-table-pagination"><span id="tc-page-note"></span><div><button id="tc-previous" class="button small">上一页</button> <button id="tc-next" class="button small">下一页</button></div></div></div>`;view.append(tcPanel);

  const importCancel=element('button','button','取消');importCancel.id='rule-import-cancel';importCancel.type='button';$('rule-read').after(importCancel);
  const importClear=addExcelImportClear('rule-import-clear','rule-file',importCancel,()=>{
    if(busy&&!importReading)return;discardRuleImport();toast('已清空本次规则读取');
  });
  let importReading=false;
  let data = null, catalog = [], configuration = null, busy = false, polling = false, preview = null, page = 1, errorPage = 1, editing = null, editRevision = '', epoch = 0, importEpoch = 0;
  let bindingDraft={},tcData=null,tcPage=1,tcBusy=false;
  let judgmentPage=1,displayError='';
  let globalData=null,globalEditing=null,globalBusy=false,globalPolling=false;
  const label = { Normal: '正常', Unusual: '异常提醒', Error: '故障', SeriousError: '严重故障' };
  function td(row, text, cls) { const cell = element('td', cls, text); row.append(cell); return cell; }
  function formattedValue(row) {
    const p=catalog.find(p=>p.Id===row.parameterId), text=row.value;
    if(!text || !p || (p.Scale===1 && !p.Bias && !['float32','float64'].includes(p.Type)))return text || '—';
    const value=Number(text);if(!Number.isFinite(value))return text;
    return value.toLocaleString('en-US',{useGrouping:false,maximumFractionDigits:Math.min(12,p.Precision ?? 6)});
  }
  function controls() {
    importCancel.disabled=(busy&&!importReading)||!(importReading||preview||$('rule-file').files.length);
    importClear.disabled=busy&&!importReading;
    const running=!!data?.execution?.running;
    ['rule-new','rule-read','rule-add','rule-replace','rule-save'].forEach(id=>$(id).disabled=busy || (id==='rule-new' && (!data || !catalog.length)) || (['rule-add','rule-replace'].includes(id) && !preview?.accepted));
    $('rule-start').disabled=busy || running || !(data?.effectiveEnabled??data?.enabled) || !['live','demo'].includes(app.state?.mode);
    $('rule-stop').disabled=busy || !running;
    document.querySelectorAll('.tc-save,.tc-code').forEach(el=>el.disabled=tcBusy);
    document.querySelectorAll('.global-edit').forEach(el=>el.disabled=globalBusy || !globalData?.canModify || !app.connected);
    $('global-refresh').disabled=globalBusy;$('global-save').disabled=globalBusy;
  }
  function displayStatus(row){
    if(!row.rule.Enabled)return {key:'disabled',text:'已停用',cls:'neutral'};
    if(displayError)return {key:'unavailable',text:'获取失败',cls:'bad'};
    if(!app.connected)return {key:'disconnected',text:'连接中断',cls:'bad'};
    if(row.error)return {key:'config-error',text:'配置错误',cls:'bad'};
    if(!data.execution?.running)return {key:'stopped',text:'判读未运行',cls:'neutral'};
    if(row.status==='执行异常')return {key:'runtime-error',text:'执行异常',cls:'bad'};
    if(row.status==='等待数据')return {key:'waiting',text:'等待数据',cls:'warn'};
    if(row.result==='Normal')return {key:'passed',text:'通过',cls:'good'};
    if(row.result)return {key:'failed',text:'未通过 · '+(label[row.result] || row.result),cls:'bad'};
    return {key:'pending',text:'尚未判读',cls:'neutral'};
  }
  function renderDisplay(){
    if(!data)return;
    const q=$('judgment-search').value.trim().toLowerCase(),level=$('judgment-level-filter').value,enabled=$('judgment-enabled-filter').value,status=$('judgment-status-filter').value;
    const all=data.rows.map(row=>({row,state:displayStatus(row)}));
    const filtered=all.filter(({row:r,state:s})=>(!q || [r.rule.Code,r.rule.Name,r.reference].join(' ').toLowerCase().includes(q)) && (!level || r.rule.Level===level) && (!enabled || r.rule.Enabled===(enabled==='enabled')) && (!status || s.key===status));
    const pages=Math.max(1,Math.ceil(filtered.length/50));judgmentPage=Math.min(judgmentPage,pages);const fragment=document.createDocumentFragment();
    for(const {row:r,state:s} of filtered.slice((judgmentPage-1)*50,judgmentPage*50)){
      const tr=element('tr');tr.dataset.ruleId=r.rule.Id;tr.dataset.status=s.key;
      const code=td(tr,r.rule.Code,'mono');code.append(element('div','param-code',r.reference));td(tr,r.rule.Name,'param-name');
      const importance=td(tr,'');importance.append(element('span','importance-tag importance-'+({'一般':'general','重要':'important','关键':'critical'}[r.rule.Level] || 'general'),r.rule.Level));
      td(tr,r.rule.Enabled?'已开启':'未开启');const value=td(tr,formattedValue(r),'mono');value.title=r.value || '';
      const verdict=td(tr,'');verdict.append(element('span','status-tag '+s.cls,s.text));if(r.error || r.detail)verdict.append(element('div','judgment-detail',r.error || r.detail));
      if((r.timings || []).length || r.currentState){const progress=element('details','judgment-progress');progress.append(element('summary','','条件进度'));if(r.currentState!==null && r.currentState!==undefined)progress.append(element('div','','工作状态 '+r.currentState));for(const t of r.timings || [])progress.append(element('div','',t.kind+(t.kind==='FRAMES'?' '+t.count+'/'+t.amount+'帧':t.remainingSeconds!==null?' 剩余'+t.remainingSeconds.toFixed(2)+'秒':t.value?' 已满足':' 等待条件')));verdict.append(progress);}
      td(tr,r.time || '—','param-time');fragment.append(tr);
    }
    if(!filtered.length){const tr=element('tr');td(tr,data.rows.length?'没有匹配的参数，请调整筛选条件。':'暂无已配置判读参数，请在“系统配置 → 判读配置”中建立规则。','judgment-empty').colSpan=7;fragment.append(tr);}
    $('judgment-rows').replaceChildren(fragment);$('judgment-count').textContent=data.total;
    const count=key=>all.filter(item=>item.state.key===key).length;
    $('judgment-summary').replaceChildren(...[['已配置',data.total],['已开启',data.enabled],['通过',count('passed')],['未通过',count('failed')],['等待数据',count('waiting')]].map(([title,total])=>{const item=element('span','judgment-summary-item');item.append(document.createTextNode(title+' '),element('strong','',total));return item;}));
    $('judgment-table-summary').textContent='筛选显示 '+filtered.length+' / 共 '+data.total+' 项';$('judgment-page-note').textContent='第'+judgmentPage+'/'+pages+'页 · 每页50项';$('judgment-previous').disabled=judgmentPage<=1;$('judgment-next').disabled=judgmentPage>=pages;
    $('judgment-error').textContent=displayError || data.storageError || '';controls();
  }
  ['judgment-search','judgment-level-filter','judgment-enabled-filter','judgment-status-filter'].forEach(id=>$(id).addEventListener(id==='judgment-search'?'input':'change',()=>{judgmentPage=1;renderDisplay();}));
  $('judgment-previous').addEventListener('click',()=>{judgmentPage--;renderDisplay();});$('judgment-next').addEventListener('click',()=>{judgmentPage++;renderDisplay();});$('judgment-refresh').addEventListener('click',()=>load());
  function render() {
    if (!data) return;
    const q = $('rule-search').value.trim().toLowerCase(), level = $('rule-level-filter').value, enabled = $('rule-enabled-filter').value;
    const rows = data.rows.filter(r => (!level || r.rule.Level === level) && (!enabled || r.rule.Enabled === (enabled === 'enabled')) && [r.rule.Code,r.rule.Name,r.reference,r.rule.Script].join(' ').toLowerCase().includes(q));
    const pages = Math.max(1, Math.ceil(rows.length / 50)); page = Math.min(page, pages); const fragment = document.createDocumentFragment();
    for (const r of rows.slice((page-1)*50, page*50)) {
      const tr = element('tr'); tr.dataset.ruleId = r.rule.Id;
      const name = td(tr, r.rule.Name); name.append(element('div', 'mono small-text', r.reference + '.value'), element('div','muted small-text', r.sourceIP || '来源待绑定'));
      td(tr, r.rule.Level); const checkbox = element('input'); checkbox.type = 'checkbox'; checkbox.checked = r.rule.Enabled; checkbox.disabled = busy || (!r.canExecute && !r.rule.Enabled); checkbox.setAttribute('aria-label', r.reference + '是否判读');
      checkbox.addEventListener('change', () => mutate('toggle', { id: r.rule.Id, enabled: checkbox.checked })); td(tr,'').append(checkbox);
      const validity=td(tr,'');validity.append(element('span','status-tag '+(r.unconfigured || r.initialTemplate?'neutral':r.canExecute?'good':'bad'),r.rule.MigrationPending?'待迁移':r.unconfigured?'未配置':r.initialTemplate?(r.canExecute?'默认三域':'默认三域 · 待绑定'):r.canExecute?'检查通过':'需修改'));if(r.unconfigured)validity.append(element('div','muted small-text','规则：无；填写有效规则后可开启'));if(r.initialTemplate)validity.append(element('div','muted small-text',r.canExecute?'this.state=0；RET=Normal；当前未判读':r.detail || '请关联SPP并明确来源后开启'));if(r.error)validity.append(element('div','muted small-text',r.error));
      const actions=td(tr,'');const edit=element('button','button small',r.rule.MigrationPending?'迁移来源':'编辑');edit.disabled=busy;edit.addEventListener('click',()=>r.rule.MigrationPending?window.NamespaceMigrationUI.open(r.rule.Id):open(r));actions.append(edit);const remove=element('button','text-button','删除');remove.disabled=busy;remove.addEventListener('click',()=>mutate('delete',{id:r.rule.Id}));actions.append(remove);fragment.append(tr);
    }
    if (!rows.length) { const tr=element('tr');const cell=td(tr,'暂无规则，可从Excel导入或手动新增。','muted');cell.colSpan=5;fragment.append(tr); }
    $('rule-rows').replaceChildren(fragment);$('rule-summary').replaceChildren(element('strong','',data.total+'条规则'),element('span','',data.enabled+'条已启用'),element('span','',(data.total-data.enabled)+'条已停用'),element('span','',data.rows.filter(r=>!!r.error).length+'条配置需处理'));
    $('rule-page-note').textContent='筛选结果 '+rows.length+'条 · 第'+page+'/'+pages+'页';$('rule-previous').disabled=page<=1;$('rule-next').disabled=page>=pages;
    $('rule-error').textContent=data.storageError || '';
    $('rule-run-status').textContent=data.execution?.running?'判读运行中':'判读未运行';$('rule-run-status').className='status-tag '+(data.execution?.running?'good':'neutral');
    $('rule-run-detail').textContent=(data.execution?.reason || '')+(data.execution?.startedAt?' · 已运行'+Number(data.execution.elapsedSeconds).toFixed(1)+'秒':'');renderDisplay();controls();
  }
  async function load(full = true) {
    if (!app.user || polling) return; polling=true; const version=epoch;
    try {
      const result=await api('continuous-rules'); if(version!==epoch)return;data=result;displayError='';
      if(full){[catalog,configuration]=await Promise.all([api('continuous-rules/parameters'),api('config')]);if(version!==epoch)return;fillSources($('rule-import-source'),$('rule-import-source').value);loadTC();}
      render();if(app.view==='judgments')loadGlobals();
    } catch(e){if(version===epoch){displayError=e.message;$('rule-error').textContent=e.message;renderDisplay();}} finally{polling=false;}
  }
  function fillSources(select,value='') { select.replaceChildren();const auto=element('option','','自动（跟随所选参数的SPP来源）');auto.value='';select.append(auto);for(const ip of configuration?.SourceIPs || []){const option=element('option','',ip);option.value=ip;select.append(option);} if(value&&!Array.from(select.options).some(o=>o.value===value)){const old=element('option','',value+'（原绑定，已不在监听配置）');old.value=value;select.append(old);}select.value=value; }
  async function mutate(action,body) {
    if(busy)return;busy=true;controls();try{await api('continuous-rules/'+action,{...body,revision:data.revision});await load(false);toast('规则已更新');}catch(e){$('rule-error').textContent=e.message;toast(e.message,true);await load(false);}finally{busy=false;render();}
  }
  function currentParameter(){return catalog.find(p=>p.Id===$('rule-parameter').value);}
  function selectionChanged(){const p=currentParameter();if(!p)return;$('rule-name').value=p.Name;$('rule-reference').textContent=parameterValueReference(p)+'；本参数规则可用this.value';sourceHint();renderBindings();}
  let sourceHintRequest=0;
  async function sourceHint(){const p=currentParameter(),request=++sourceHintRequest;if(!p)return;$('rule-source-note').textContent='自动沿用参数所属SPP的来源，无需手选IP。';try{const value=await api('continuous-rules/source?parameterId='+encodeURIComponent(p.Id));if(request!==sourceHintRequest||currentParameter()?.Id!==p.Id)return;const manual=$('rule-source').value;$('rule-source-note').textContent=manual?'手动限定来源：'+manual+'；选择“自动”可改为跟随SPP。':value.canAuto?'自动来源：'+value.sourceIP+(value.packet?' · '+value.packet:'')+'；保存后继续跟随SPP配置。':value.error;}catch(e){if(request===sourceHintRequest)$('rule-source-note').textContent=e.message;}}
  $('rule-source').addEventListener('change',sourceHint);
  function open(row=null) {
    editing=row?.rule || null;bindingDraft=JSON.parse(JSON.stringify(editing?.Bindings || {}));editRevision=data.revision;$('rule-dialog-title').textContent=editing?'编辑常态化规则':'新增常态化规则';$('rule-editor-error').textContent='';
    $('rule-parameter').replaceChildren();for(const p of catalog){const option=element('option','',parameterReference(p)+' / '+p.Name+(p.PacketName?' / '+p.PacketName:''));option.value=p.Id;$('rule-parameter').append(option);}
    if(row?.parameterId)$('rule-parameter').value=row.parameterId;selectionChanged();$('rule-name').value=editing?.Name || currentParameter()?.Name || '';fillSources($('rule-source'),editing?.SourceIP || '');$('rule-level').value=editing?.Level || '一般';$('rule-enabled').checked=editing?.Enabled || false;
    sourceHint();$('rule-script').value=editing ? (editing.Script || '') : (data?.defaultScript || '');$('rule-description').value=editing?.Description || '';renderBindings();dialog.showModal();
  }
  $('rule-form').addEventListener('submit',async event=>{
    event.preventDefault();if(busy)return;const p=currentParameter();if(!p){$('rule-editor-error').textContent='请选择存在的解码参数';return;}busy=true;controls();
    try{const rule={...(editing || {}),Id:editing?.Id || '',Code:parameterReference(p),Name:$('rule-name').value.trim(),Level:$('rule-level').value,Enabled:$('rule-enabled').checked,ParameterId:p.Id,SourceIP:$('rule-source').value,Script:$('rule-script').value,Description:$('rule-description').value,Bindings:collectBindings()};await api('continuous-rules/save',{rule,revision:editRevision});dialog.close();await load(false);toast('规则已检查并保存');}
    catch(e){$('rule-editor-error').textContent=e.message;}finally{busy=false;render();}
  });
  function revokeRulePreview(token){if(token)api('continuous-rules/preview/cancel',{token}).catch(()=>{});}
  function discardRuleImport(clearFile=true){
    revokeRulePreview(preview?.token);importEpoch++;if(importReading){importReading=false;busy=false;}preview=null;errorPage=1;
    if(clearFile)$('rule-file').value='';$('rule-preview').classList.add('hidden');$('rule-import-error').textContent='';
    for(const id of ['rule-import-summary','rule-preview-rows','rule-error-rows','rule-import-conflicts'])$(id).replaceChildren();
    for(const id of ['rule-preview-note','rule-errors-summary','rule-error-page-note'])$(id).textContent='';
    $('rule-errors-details').open=false;$('rule-error-previous').disabled=true;$('rule-error-next').disabled=true;
    $('rule-errors-export').removeAttribute('href');controls();
  }
  importCancel.addEventListener('click',()=>{if(busy&&!importReading)return;discardRuleImport();toast('已取消规则导入，已保存规则未修改');});
  function renderErrors(){if(!preview)return;const pages=Math.max(1,Math.ceil(preview.errors.length/50));errorPage=Math.min(errorPage,pages);const fragment=document.createDocumentFragment();for(const e of preview.errors.slice((errorPage-1)*50,errorPage*50)){const tr=element('tr');td(tr,e.Sheet+' / 第'+e.Row+'行');td(tr,e.Code,'mono');td(tr,e.Message);fragment.append(tr);}$('rule-error-rows').replaceChildren(fragment);$('rule-errors-summary').textContent='错误行 / '+preview.rejected+'条（不会导入）';$('rule-errors-details').open=preview.rejected>0;$('rule-error-page-note').textContent='第'+errorPage+'/'+pages+'页 · 全部'+preview.rejected+'条';$('rule-error-previous').disabled=errorPage<=1;$('rule-error-next').disabled=errorPage>=pages;}
  $('rule-read').addEventListener('click',async()=>{
    const file=$('rule-file').files[0];if(!file){$('rule-import-error').textContent='请选择规则Excel';return;}if(busy)return;discardRuleImport(false);busy=true;importReading=true;controls();const version=++importEpoch;preview=null;$('rule-preview').classList.add('hidden');$('rule-import-error').textContent='';
    try{const response=await fetch('/api/continuous-rules/preview?filename='+encodeURIComponent(file.name)+'&sourceIP='+encodeURIComponent($('rule-import-source').value),{method:'POST',credentials:'same-origin',headers:{'Content-Type':'application/octet-stream','X-CSRF-Token':app.user.csrf},body:file});const result=await response.json();if(!response.ok)throw new Error(result.error || '规则读取失败');if(version!==importEpoch){revokeRulePreview(result.token);return;}preview=result;errorPage=1;$('rule-preview').classList.remove('hidden');$('rule-import-summary').replaceChildren(element('strong','',file.name),element('span','',result.total+'条读取'),element('span','',result.accepted+'条可导入'),element('span','',result.rejected+'条错误'),element('span','',result.duplicates+'条已有相同规则'));
      const fragment=document.createDocumentFragment();for(const r of result.rules.slice(0,100)){const tr=element('tr');td(tr,r.SourceSheet+' / 第'+r.SourceRow+'行');const field=catalog.find(p=>p.Id===r.ParameterId);td(tr,r.Name+' / '+(field?parameterReference(field):r.Code));td(tr,r.Level);td(tr,r.Enabled?'是':'否');fragment.append(tr);}$('rule-preview-rows').replaceChildren(fragment);$('rule-preview-note').textContent='显示前'+Math.min(100,result.accepted)+'条，提交包含全部'+result.accepted+'条合格规则。';$('rule-import-conflicts').replaceChildren(...result.conflicts.map(s=>element('p','issue-warning',s)));$('rule-errors-export').href='/api/continuous-rules/errors?token='+encodeURIComponent(result.token);renderErrors();
    }catch(e){if(version===importEpoch)$('rule-import-error').textContent=e.message;}finally{if(version===importEpoch){importReading=false;busy=false;controls();}}
  });
  async function apply(mode){if(busy || !preview)return;busy=true;controls();try{await api('continuous-rules/apply',{token:preview.token,mode,enablePolicy:$('rule-import-policy').value});preview=null;$('rule-preview').classList.add('hidden');await load(false);toast('规则已'+(mode==='add'?'添加':'替换'));}catch(e){$('rule-import-error').textContent=e.message;}finally{busy=false;controls();}}
  $('rule-add').addEventListener('click',()=>apply('add'));$('rule-replace').addEventListener('click',()=>apply('replace'));
  $('rule-file').addEventListener('change',()=>discardRuleImport(false));$('rule-import-source').addEventListener('change',()=>discardRuleImport(false));
  $('rule-new').addEventListener('click',()=>open());$('rule-refresh').addEventListener('click',()=>load());$('rule-parameter').addEventListener('change',selectionChanged);
  ['rule-search','rule-level-filter','rule-enabled-filter'].forEach(id=>$(id).addEventListener(id==='rule-search'?'input':'change',()=>{page=1;render();}));
  $('rule-previous').addEventListener('click',()=>{page--;render();});$('rule-next').addEventListener('click',()=>{page++;render();});$('rule-error-previous').addEventListener('click',()=>{errorPage--;renderErrors();});$('rule-error-next').addEventListener('click',()=>{errorPage++;renderErrors();});
  ['rule-editor-close','rule-editor-cancel'].forEach(id=>$(id).addEventListener('click',()=>dialog.close()));
  $('rule-template-if').addEventListener('click',()=>{
    $('rule-script').value='InitRegion\n    this.state=0;\nEndInitRegion\n\nStateRegion\n\nEndStateRegion\n\nResultRegion\n    if(this.value > 0) then\n        RET=Normal;\n    else\n        RET=Error;\n    end\nEndResultRegion';
    renderBindings();
  });
  $('rule-template-range').addEventListener('click',()=>{$('rule-script').value='RANGE=[0 1];';renderBindings();});$('rule-template-enum').addEventListener('click',()=>{$('rule-script').value='if(INENUM(this.value)) then\n    RET=Normal;\nelse\n    RET=Error;\nend';renderBindings();});
  function aliases(){const text=$('rule-script').value.replace(/\/\/[^\r\n]*/g,'');return [...new Set(text.match(/(?:TME|TMB|TM)\.[\p{L}_][\p{L}\p{N}_]*/gu) || [])].filter(name=>name!==parameterReference(currentParameter() || {}));}
  function renderBindings(){const fragment=document.createDocumentFragment();for(const name of aliases()){
    const line=element('div','rule-form-grid'),field=element('label','',name),select=element('select');const blank=element('option','','请选择明确参数');blank.value='';select.append(blank);const candidates=catalog.filter(p=>parameterReference(p)===name);for(const p of candidates){const option=element('option','',(p.PacketName?p.PacketName+' / ':'')+p.Group+' / '+p.Name);option.value=p.Id;select.append(option);}const chosen=bindingDraft[name] || {};select.value=chosen.ParameterId || (candidates.length===1?candidates[0].Id:'');const sourceLabel=element('label','','来源IP'),source=element('select');fillSources(source,chosen.SourceIP || '');source.options[0].textContent='沿用主参数 / SPP来源';
    const update=()=>bindingDraft[name]={ParameterId:select.value,SourceIP:source.value};select.addEventListener('change',update);source.addEventListener('change',update);update();field.append(select);sourceLabel.append(source);line.append(field,sourceLabel);fragment.append(line);
  }if(!fragment.childNodes.length)fragment.append(element('p','muted small-text','当前脚本仅使用主参数，无需附加绑定。'));$('rule-binding-fields').replaceChildren(fragment);}
  function collectBindings(){const result={};for(const name of aliases())if(bindingDraft[name]?.ParameterId)result[name]=bindingDraft[name];return result;}
  $('rule-script').addEventListener('input',renderBindings);
  $('rule-script').addEventListener('keydown',event=>{
    if(event.key!=='Tab'||event.ctrlKey||event.altKey||event.metaKey||event.isComposing)return;
    const editor=event.currentTarget;if(editor.disabled||editor.readOnly)return;event.preventDefault();
    const text=editor.value,start=editor.selectionStart,end=editor.selectionEnd,direction=editor.selectionDirection;
    let from=start,to=end,replacement='    ',nextStart=start+4,nextEnd=start+4;
    if(event.shiftKey||start!==end){
      from=start===0?0:text.lastIndexOf('\n',start-1)+1;
      if(start===end){const lineEnd=text.indexOf('\n',start);to=lineEnd<0?text.length:lineEnd;}
      else to=end>start&&text[end-1]==='\n'?end-1:end;
      let position=from;const edits=[];
      replacement=text.slice(from,to).split('\n').map(line=>{
        const removed=event.shiftKey?(line.match(/^(?:\t| {1,4})/)?.[0].length||0):0,added=event.shiftKey?0:4;
        edits.push({at:position,removed,added});position+=line.length+1;
        return event.shiftKey?line.slice(removed):'    '+line;
      }).join('\n');
      const mapped=position=>position+edits.reduce((delta,edit)=>delta+(position>edit.at?edit.added:0)-Math.min(edit.removed,Math.max(0,position-edit.at)),0);
      nextStart=mapped(start);nextEnd=mapped(end);
    }
    if(text.slice(from,to)===replacement)return;
    if(editor.maxLength>=0&&text.length-(to-from)+replacement.length>editor.maxLength){setText('rule-editor-error','规则长度最多'+editor.maxLength+'个字符');return;}
    const top=editor.scrollTop,left=editor.scrollLeft;editor.setSelectionRange(from,to);
    // Native insertion preserves the browser's undo history for keyboard edits.
    document.execCommand('insertText',false,replacement);
    if(editor.value===text){editor.setRangeText(replacement,from,to,'end');editor.dispatchEvent(new Event('input',{bubbles:true}));}
    editor.setSelectionRange(nextStart,nextEnd,direction);editor.scrollTop=top;editor.scrollLeft=left;
  });
  async function judgment(action){if(busy)return;busy=true;controls();try{await api('continuous-rules/control',{action});await load(false);toast(action==='start'?'判读已启动':'判读已停止，参数监听继续');}catch(e){toast(e.message,true);$('rule-error').textContent=e.message;}finally{busy=false;render();}}
  $('rule-start').addEventListener('click',()=>judgment('start'));$('rule-stop').addEventListener('click',()=>judgment('stop'));
  async function loadTC(){if(!app.user || tcBusy)return;tcBusy=true;const version=epoch;try{const result=await api('continuous-rules/tc?page='+tcPage+'&search='+encodeURIComponent($('tc-search').value));if(version!==epoch)return;tcData=result;tcPage=result.page;$('tc-summary').textContent='已采集'+result.total+'条不同指令 · 来源'+result.sourceIP;$('tc-error').textContent=result.error || '';const fragment=document.createDocumentFragment();for(const r of result.rows){const tr=element('tr');tr.dataset.commandNumber=r.number;td(tr,r.number+' / '+r.name);const packetCell=td(tr,(r.destinationIPs || [r.destinationIP]).join(', ')+' / '+r.length+'字节');packetCell.append(element('div','mono small-text',r.hexPreview));const detail=element('details','import-details');detail.append(element('summary','','完整UDP载荷'));const payload=element('pre','mono small-text');payload.style.whiteSpace='pre-wrap';payload.style.overflowWrap='anywhere';detail.append(payload);detail.addEventListener('toggle',async()=>{if(!detail.open || detail.dataset.loaded)return;try{const packet=await api('continuous-rules/tc/detail?number='+r.number);payload.textContent=packet.payloadHex || '（空UDP载荷）';detail.dataset.loaded='yes';}catch(e){payload.textContent=e.message;}});packetCell.append(detail);if((r.aliases || []).length)packetCell.append(element('div','muted small-text','合并的历史指令：'+r.aliases.map(a=>a.Number+' / '+a.Name).join('；')));if((r.automaticCodes || []).length)packetCell.append(element('div','muted small-text','自动代号：'+r.automaticCodes.map(code=>'TC.'+code).join('、')));if((r.manualCodes || r.codes || []).length>1)packetCell.append(element('div','muted small-text','保留手动代号：'+(r.manualCodes || r.codes).map(code=>'TC.'+code).join('、')+'；保存将替换手动代号，自动代号保留'));const input=element('input','tc-code');input.value=r.code;input.placeholder='例如K001';input.maxLength=67;input.setAttribute('aria-label','指令'+r.number+'的TC代号');td(tr,'').append(input);const save=element('button','button small tc-save','保存绑定');save.addEventListener('click',async()=>{try{await api('continuous-rules/tc',{number:r.number,code:input.value,revision:tcData.revision});await loadTC();await load(false);toast('TC绑定已保存');}catch(e){$('tc-error').textContent=e.message;}});td(tr,'').append(save);fragment.append(tr);}if(!result.rows.length){const tr=element('tr');td(tr,'尚无指令，请先在接收概览配置发送监视并采集。','muted').colSpan=4;fragment.append(tr);}$('tc-rows').replaceChildren(fragment);$('tc-page-note').textContent='第'+result.page+'/'+result.pages+'页';$('tc-previous').disabled=tcPage<=1;$('tc-next').disabled=tcPage>=result.pages;}catch(e){$('tc-error').textContent=e.message;}finally{tcBusy=false;controls();}}
  function renderGlobals(){
    if(!globalData)return;const fragment=document.createDocumentFragment();
    for(const row of globalData.rows){const tr=element('tr');tr.dataset.globalName=row.name;td(tr,row.name,'mono');const current=td(tr,row.hasValue?row.valueText:'尚未赋值','mono');current.append(element('div','muted small-text',row.hasValue?({number:'数值',boolean:'布尔',result:'判读状态',nil:'NIL'})[row.kind]:'NIL'));if(row.prepared)current.append(element('div','muted small-text','下次启动的预设值'));
      td(tr,row.writers.join('\n') || '人工设置').style.whiteSpace='pre-line';td(tr,row.readers.join('\n') || '—').style.whiteSpace='pre-line';const time=td(tr,row.time || '—','param-time');if(row.actor)time.append(element('div','muted small-text',row.actor));const button=element('button','button small global-edit','修改状态');button.addEventListener('click',()=>openGlobal(row));td(tr,'').append(button);fragment.append(tr);}
    if(!globalData.rows.length){const tr=element('tr');td(tr,'当前规则未使用共享变量，例如gzReady或gzMode。','muted').colSpan=6;fragment.append(tr);}
    $('global-rows').replaceChildren(fragment);$('global-count').textContent=globalData.rows.length;$('global-mode').textContent=globalData.canModify?(globalData.running?'监听与判读运行中，可直接修改共享变量':'监听运行中，可预设下一次判读的共享变量'):'监听未启动；启动后可修改共享变量';$('global-error').textContent=globalData.errors.length?'以下规则语法无效，暂不能提取变量：'+globalData.errors.join('；'):'';controls();
  }
  async function loadGlobals(){if(!app.user || globalPolling || globalBusy)return;globalPolling=true;const version=epoch;try{const result=await api('continuous-rules/globals');if(version!==epoch)return;globalData=result;renderGlobals();}catch(e){if(version===epoch){globalData=null;$('global-error').textContent=e.message;controls();}}finally{globalPolling=false;}}
  async function openGlobal(row){
    if(globalBusy)return;globalBusy=true;controls();const version=epoch;
    try{const latest=await api('continuous-rules/globals');if(version!==epoch)return;globalData=latest;const current=latest.rows.find(r=>r.name===row.name);if(!latest.canModify || !current)throw new Error('监听或变量配置已变化，请刷新后重试');globalEditing={...current,generation:latest.generation};$('global-edit-name').textContent=current.name;$('global-old-value').textContent='当前值：'+(current.hasValue?current.valueText:'尚未赋值（NIL）');$('global-kind').value=current.hasValue?current.kind:'number';$('global-value').value=current.hasValue && current.kind!=='nil'?current.valueText:'0';$('global-editor-error').textContent='';globalKindChanged();renderGlobals();globalDialog.showModal();}
    catch(e){if(version===epoch)$('global-error').textContent=e.message;}finally{globalBusy=false;controls();}
  }
  function globalKindChanged(){const kind=$('global-kind').value;$('global-value').disabled=kind==='nil';$('global-value').placeholder=kind==='boolean'?'true或false':kind==='result'?'Normal、Unusual、Error或SeriousError':'例如0、1或2.5';}
  $('global-kind').addEventListener('change',globalKindChanged);$('global-refresh').addEventListener('click',loadGlobals);['global-close','global-cancel'].forEach(id=>$(id).addEventListener('click',()=>globalDialog.close()));
  $('global-form').addEventListener('submit',async event=>{event.preventDefault();if(globalBusy || !globalEditing)return;globalBusy=true;controls();const version=epoch;try{const result=await api('continuous-rules/globals',{name:globalEditing.name,kind:$('global-kind').value,value:$('global-value').value,revision:globalEditing.revision,generation:globalEditing.generation});if(version!==epoch)return;globalData=result;globalDialog.close();renderGlobals();await load(false);toast('共享变量状态已更新');}catch(e){if(version===epoch)$('global-editor-error').textContent=e.message;}finally{globalBusy=false;controls();}});
  $('tc-refresh').addEventListener('click',loadTC);$('tc-search-button').addEventListener('click',()=>{tcPage=1;loadTC();});$('tc-previous').addEventListener('click',()=>{tcPage--;loadTC();});$('tc-next').addEventListener('click',()=>{tcPage++;loadTC();});
  setInterval(()=>{if(app.user && ['judgments','results'].includes(app.view) && !busy)load(false);},1000);
  function reset(){epoch++;importEpoch++;importReading=false;busy=false;data=null;preview=null;editing=null;bindingDraft={};tcData=null;displayError='';judgmentPage=1;globalData=null;globalEditing=null;globalDialog.close();$('global-rows').replaceChildren();$('global-error').textContent='';$('judgment-rows').replaceChildren();dialog.close();$('rule-preview').classList.add('hidden');$('rule-rows').replaceChildren();$('rule-error').textContent='';}
  controls();return {load,reset,renderConnection:()=>{controls();if(app.view==='results')renderDisplay();}};
})();
