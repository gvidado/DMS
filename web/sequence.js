'use strict';
window.SequenceUI=(()=>{
  names['sequence-config']=['序列配置','读取七列或十一列Excel，检查串行、并行结构与参数、指令引用。'];
  names['sequence-run']=['序列运行','从启动页面创建任务，每次启动在独立页签内显示运行情况。'];
  names['sequence-execute']=['序列执行','主动发送指令；CALL可启动独立判读，执行继续。'];
  const configView=element('section','view hidden');configView.id='view-sequence-config';
  configView.innerHTML=`<section class="panel"><div class="panel-heading"><div><h2>序列配置与Excel导入</h2><p>序列执行主动发送指令；序列判读仅监视。并行段内部均只监视、不主动发送。</p></div><button class="button" id="sequence-go-run">序列运行</button></div><div class="section-pad"><div class="sequence-form-grid"><label>已保存序列<select id="sequence-config-select"><option value="">新建导入</option></select></label><label>运行模式<select id="sequence-mode"><option value="judge">序列判读 · 仅监视</option><option value="execute">序列执行 · 主动发送</option></select></label><label>统一默认超时（秒）<input id="sequence-timeout" type="number" min="0.001" max="604800" step="any" value="30"></label><label>参数来源<select id="sequence-source"><option value="">按SPP来源自动绑定</option></select></label></div><p class="muted small-text" id="sequence-mode-hint"></p><div class="rule-toolbar"><button id="sequence-settings-save" class="button">保存运行设置</button><button id="sequence-delete" class="button danger">删除所选序列</button><button id="sequence-reload" class="button">重新载入</button></div><p class="error-text" id="sequence-config-error"></p><hr class="sequence-divider"><div class="rule-toolbar"><input id="sequence-file" type="file" accept=".xlsx"><select id="sequence-import-policy" aria-label="导入方式"><option value="add">添加序列</option><option value="replace">替换全部序列</option></select><button id="sequence-read" class="button">读取Excel预览</button><button id="sequence-convert" class="button">小工具：读取并转换指令</button><button id="sequence-convert-export" class="button" disabled>导出转换结果Excel</button><button id="sequence-apply" class="button primary" disabled>保存勾选工作表</button></div><p class="muted small-text">支持标准七列A～G；新十一列表读取A关键字、B测试工作内容、C指令、D判读、F标志、G期望执行时间、H超时时间，忽略E/I/J/K。预览与转换导出统一为原七列格式。判据可直接写TME.参数代号或TMB.参数代号，省略.value（兼容旧写法）；只取当前工程值，支持与 && / AND、或 || / OR、非 ! / NOT 和括号，例如 TME.A1 &gt; 0 &amp;&amp; INENUM(TMB.A2)。期望执行时间可填数字、T0:n或F:n，例如F:2为本序列开始后2秒。执行序列可在任意步骤以CALL启动XLP判读序列，随后继续执行；判读在独立页内标签运行。相同模式的CALL仍为末尾跳转。指令列填写目标序列号，目标独立计时并重新建立T0。</p><p id="sequence-import-error" class="form-error" role="alert" style="white-space:pre-wrap"></p><details id="sequence-conversion-panel" class="hidden"><summary>指令转换报告（首次读取时的结果）</summary><p class="muted small-text">错放在其他动作列的独立T0移至标志列；T0:n、F:n保留在期望执行时间列。仅按库内指令代号匹配，不按名称匹配。优先采用指令列已命中的库内编号；未命中时从说明列中的BC/YKE/PSS编号补救，仍未命中才置TEXT。控制行和纯参数判读行保留。PLUSE先暂停，人工继续后发送本行指令，再进入下一行。指令列为空时，说明中出现唯一库内编号（如BC2133或bc2133）就补填，无需“发送”字样；多个编号列出候选供人工选择。唯一代号匹配时转换；说明列或指令列匹配库内代号时，TEXT关键字改为空；两列均未匹配到代号的指令行设为TEXT并保留原文。多个候选或两列内容不一致时，请在预览中人工核对。指令列存在未入库的YKE/YKM时显示原表行号提示，并自动展开此报告；请确认代号，原文保留。转换和导出不保存或执行序列；导出包含所有已读取工作表，按标准七列A～G保存，新表的E/I/J/K内容不会带入。</p><div id="sequence-conversion-report"></div></details><div id="sequence-import-sheets"></div></div></section><section class="panel"><div class="panel-heading"><div><h2>配置检查与发送目标</h2><p>检查不会发送任何指令；有错误的工作表不能保存或启动。</p></div></div><div class="section-pad" id="sequence-validation">选择已保存序列，或读取Excel。</div></section><section class="panel"><div class="panel-heading"><h2 id="sequence-preview-title">读取后预览与修改</h2><div class="rule-toolbar"><button id="sequence-save-edits" class="button primary hidden" disabled>保存序列修改</button><button id="sequence-cancel-edits" class="button hidden" disabled>取消修改</button><button id="sequence-recheck" class="button primary" disabled>重新检测当前工作表</button><button id="sequence-preview-prev" class="button small">上一页</button><span id="sequence-preview-page"></span><button id="sequence-preview-next" class="button small">下一页</button></div></div><div class="table-scroll sequence-table-scroll"><table class="data-table sequence-table"><thead><tr><th>原行号</th><th>关键字</th><th>测试工作程序内容及要求</th><th>指令</th><th>判读要求</th><th>标志</th><th>期望执行时间</th><th>超时时间</th></tr></thead><tbody id="sequence-preview-rows"></tbody></table></div></section>`;
  const importGroupField=element('label','sequence-import-group','导入分组');
  importGroupField.insertAdjacentHTML('beforeend','<input id="sequence-import-group" list="sequence-group-names" maxlength="80" placeholder="选择或新建分组" autocomplete="off"><datalist id="sequence-group-names"></datalist>');
  configView.querySelector('#sequence-import-policy').after(importGroupField);
  configView.querySelector('#sequence-file').multiple=true;
  configView.querySelector('#sequence-file').setAttribute('aria-label','选择序列Excel文件，可多选');
  configView.querySelector('#sequence-read').textContent='读取Excel预览（可多选）';
  const importProgress=element('p','muted small-text');importProgress.setAttribute('role','status');importProgress.id='sequence-import-progress';
  configView.querySelector('#sequence-import-error').before(importProgress);
  const selectBatchLabel=element('label','sequence-batch-select hidden');
  const selectBatch=element('input');selectBatch.type='checkbox';
  selectBatchLabel.append(selectBatch,document.createTextNode('勾选全部通过项'));
  configView.querySelector('#sequence-import-sheets').before(selectBatchLabel);
  const previewMeta=element('div','section-pad');
  previewMeta.innerHTML='<div class="sequence-form-grid"><label>序列名称<input id="sequence-preview-name" maxlength="240" autocomplete="off" disabled><small class="muted">修改名称后需重新检测。</small></label><label>保存分组<input id="sequence-preview-group" list="sequence-group-names" maxlength="80" placeholder="留空为未分组" autocomplete="off" disabled><small class="muted">可选择已有分组或输入新分组。</small></label></div>';
  configView.querySelector('#sequence-preview-rows').closest('.table-scroll').before(previewMeta);
  const rowToolbar=element('div','sequence-row-toolbar rule-toolbar'),appendRow=element('button','button','末尾添加一行');appendRow.id='sequence-row-add';appendRow.type='button';rowToolbar.append(appendRow,element('span','muted small-text','可在每行上方或下方插入、删除。序号表示执行顺序；原表行号保留，新增行单独编号。增删后需重新检测并保存。'));previewMeta.append(rowToolbar);
  const previewHead=configView.querySelector('#sequence-preview-rows').closest('table').querySelector('thead tr');previewHead.firstElementChild.textContent='序号 / 行号';previewHead.append(element('th','','操作'));
  const runView=element('section','view hidden');runView.id='view-sequence-run';
  runView.innerHTML=`<section class="panel"><div class="panel-heading"><div><h2 id="sequence-run-heading">序列判读控制</h2><p>本页仅控制当前这次运行；其他执行、判读任务各自运行。切换页签不会停止后台任务。</p></div><button class="button" id="sequence-go-config">序列配置</button></div><div class="section-pad"><div class="rule-toolbar"><select id="sequence-run-select" aria-label="运行序列"></select><button id="sequence-start" class="button primary">开始序列</button><button id="sequence-pause" class="button" disabled>暂停</button><button id="sequence-resume" class="button" disabled>继续</button><button id="sequence-stop" class="button danger" disabled>停止本任务</button></div><p id="sequence-send-note" class="muted"></p><p id="sequence-other-runtime" class="muted" role="status"></p><div id="sequence-summary" class="sequence-summary" role="status"></div><div id="sequence-t0" class="sequence-t0" aria-label="T0时间"></div><p id="sequence-run-error" class="error-text"></p></div></section><section class="panel"><div class="panel-heading"><div><h2>逐项运行情况</h2><p id="sequence-progress"></p></div><div class="rule-toolbar"><a id="sequence-diagnostic-export" class="button small" href="/api/sequence/diagnostic" download>导出当前判读诊断</a><button id="sequence-run-prev" class="button small">上一页</button><span id="sequence-run-page"></span><button id="sequence-run-next" class="button small">下一页</button></div></div><div class="table-scroll sequence-table-scroll"><table class="data-table sequence-table"><thead><tr><th>原行号</th><th>关键字</th><th>步骤说明</th><th>指令</th><th>判读要求</th><th>超时时限</th><th>状态 / 剩余时限</th><th>实际值</th><th>实际时间 / 调度信息</th><th>异常或等待原因</th></tr></thead><tbody id="sequence-run-rows"></tbody></table></div></section><section class="panel"><div class="panel-heading"><div><h2>序列结果与异常记录</h2><p>可按序列号、名称或运行编号搜索，选择某一次历史运行查看全部步骤；同名多次运行分别列出，可筛选异常并逐条填写原因后确认，与统一异常判读记录同步。</p></div><div class="rule-toolbar"><button id="sequence-history-refresh" class="button small">刷新</button><a id="sequence-export" class="button small" href="/api/sequence/export.csv">导出筛选结果CSV</a></div></div><div class="table-scroll sequence-table-scroll"><table class="data-table"><thead><tr><th>实际时间</th><th>船时</th><th>模式 / 序列</th><th>原行号 / 事件</th><th>结果</th><th>参数实际值</th><th>详情</th><th>异常确认信息</th><th>操作</th></tr></thead><tbody id="sequence-history-rows"></tbody></table></div><div class="rule-toolbar section-pad"><button id="sequence-history-prev" class="button small">上一页</button><span id="sequence-history-page"></span><button id="sequence-history-next" class="button small">下一页</button></div></section>`;
  const historyFilters=element('div','section-pad');historyFilters.innerHTML='<div class="rule-toolbar"><input id="sequence-history-search" placeholder="搜索序列号、名称、日期或运行编号" maxlength="512" aria-label="搜索历史序列"><button id="sequence-history-query" class="button">搜索</button><select id="sequence-history-result" aria-label="序列结果筛选"><option value="all">全部记录</option><option value="abnormal">仅异常</option><option value="pending">异常未确认</option><option value="released">异常已确认</option></select><select id="sequence-history-scope" aria-label="选择历史运行" style="max-width:100%;min-width:220px;flex:1"><option value="all">全部匹配运行</option><option value="current">当前运行</option></select></div><div class="current-table-pagination"><span id="sequence-runs-page"></span><div><button id="sequence-runs-prev" class="button small">更新的运行</button> <button id="sequence-runs-next" class="button small">更早的运行</button></div></div><p id="sequence-history-filter-error" class="form-error" role="alert"></p>';
  runView.querySelector('#sequence-history-rows').closest('.table-scroll').before(historyFilters);
  const historyRuns=element('div','section-pad');historyRuns.id='sequence-history-runs';historyRuns.innerHTML='<p class="muted small-text">历史运行按次永久保留，包括服务器重启前的记录。选择“查看记录”后仅查看该次结果，不会重新启动或发送指令。</p><div class="table-scroll"><table class="data-table"><thead><tr><th>本次名称 / 序列号</th><th>运行模式</th><th>开始时间</th><th>最后记录时间</th><th>最终状态 / 记录数</th><th>操作</th></tr></thead><tbody id="sequence-history-run-list"></tbody></table></div>';
  historyFilters.after(historyRuns);
  const executeView=element('section','view hidden');executeView.id='view-sequence-execute';
  const runTabs=element('nav','sequence-task-tabs');runTabs.id='sequence-task-tabs';runTabs.setAttribute('aria-label','序列运行任务');
  const launchPanel=element('section','panel');launchPanel.id='sequence-launch-panel';
  launchPanel.innerHTML='<div class="panel-heading"><div><h2>启动序列</h2><p>每次启动均创建一个独立任务，以本次名称显示在上方页签。CALL调用的目标也会创建自己的运行页签。</p></div></div><div class="section-pad"><div class="rule-toolbar"><label>运行模式 <select id="sequence-launch-mode"><option value="execute">序列执行（主动发送）</option><option value="judge">序列判读（仅监视）</option></select></label><span id="sequence-launch-inputs" class="rule-toolbar"></span></div><p id="sequence-launch-note" class="muted">先开始监听；多个任务独立计时和控制。切换页签不会停止任务，监听停止时所有任务停止。</p></div><div class="table-scroll"><table class="data-table"><thead><tr><th>本次启动名称 / 运行编号</th><th>模式 / 序列号</th><th>启动方式</th><th>开始时间</th><th>运行状态</th><th>操作</th></tr></thead><tbody id="sequence-task-list"></tbody></table></div></section>';
  const detailPanel=element('div','sequence-task-detail');detailPanel.id='sequence-task-detail';detailPanel.append(...runView.children);
  const historyPanel=$('sequence-history-rows')||detailPanel.querySelector('#sequence-history-rows');
  const taskHistory=historyPanel.closest('section.panel');
  launchPanel.querySelector('#sequence-launch-inputs').append(detailPanel.querySelector('#sequence-run-select'),detailPanel.querySelector('#sequence-start'));
  const sendNote=detailPanel.querySelector('#sequence-send-note');launchPanel.querySelector('.section-pad').append(sendNote);
  const taskError=detailPanel.querySelector('#sequence-run-error');
  runView.append(runTabs,taskError,launchPanel,detailPanel,taskHistory);
  document.querySelector('.metric-grid').after(configView,runView,executeView);
  const startDialog=element('dialog','packet-dialog');startDialog.id='sequence-start-dialog';
  startDialog.innerHTML=`<div class="dialog-heading"><h2>序列预览</h2><button id="sequence-start-close" class="icon-button" aria-label="关闭启动确认">×</button></div><form id="sequence-start-form" class="section-pad"><p id="sequence-start-error" class="form-error" role="alert" hidden></p><div class="import-action-line"><button id="sequence-start-cancel" type="button" class="button">取消</button><button id="sequence-start-confirm" type="submit" class="button primary">确认并开始</button></div></form>`;document.body.append(startDialog);
  const importCancel=element('button','button','取消');importCancel.id='sequence-import-cancel';importCancel.type='button';$('sequence-convert').after(importCancel);
  const importClear=addExcelImportClear('sequence-import-clear','sequence-file',importCancel,()=>resetSequenceImport(false));
  let importReading=false,importToken='';
  let pendingStart=null,startSettingsFor='';
  const startOptions=element('div','sequence-start-settings');
  startOptions.innerHTML='<label>本次运行名称<input id="sequence-run-name" maxlength="128" required autocomplete="off"></label><label>起始原表行号<input id="sequence-start-row" type="number" min="1" step="1" placeholder="留空从第一项开始" autocomplete="off"></label><label>T0已过去秒数<input id="sequence-start-t0" maxlength="80" placeholder="如T0=10" autocomplete="off"></label><label>F已过去秒数<input id="sequence-start-f0" maxlength="80" placeholder="如F0=20" autocomplete="off"></label>';
  launchPanel.querySelector('#sequence-launch-inputs').closest('.rule-toolbar').after(startOptions);
  function syncStartSettings(definition){
    const id=definition?.Id||'';if(id===startSettingsFor)return;startSettingsFor=id;
    $('sequence-run-name').value=definition?.Name||'';
    for(const key of ['sequence-start-row','sequence-start-t0','sequence-start-f0'])$(key).value='';
  }
  function startError(message){const field=$('sequence-start-error');field.textContent=message;field.hidden=!message;}
  startDialog.classList.add('sequence-start-preview-dialog');
  const startPreview=element('section','sequence-start-preview');
  startPreview.innerHTML='<div class="rule-toolbar"><button type="button" id="sequence-start-preview-prev" class="button small">上一页</button><span id="sequence-start-preview-page"></span><button type="button" id="sequence-start-preview-next" class="button small">下一页</button></div><div class="table-scroll"><table class="data-table"><thead><tr><th>原行号</th><th>关键字</th><th>测试工作程序内容及要求</th><th>指令 / 动作</th><th>判读要求</th><th>标志</th><th>期望执行时间</th><th>超时时限</th></tr></thead><tbody id="sequence-start-preview-rows"></tbody></table></div>';
  $('sequence-start-error').before(startPreview);
  let startPreviewData=null,startPreviewPage=1,startPreviewLoading=false,startPreviewEpoch=0;
  function drawStartPreview(){
    const data=startPreviewData,rows=data?.definition.Rows||[],pages=Math.max(1,Math.ceil(rows.length/50));startPreviewPage=Math.min(pages,Math.max(1,startPreviewPage));
    const startRow=Number($('sequence-start-row').value),first=rows.findIndex(r=>r.Row===startRow),body=$('sequence-start-preview-rows');body.replaceChildren();
    const commands=new Map();
    for(const command of data?.commands||[])if(!commands.has(command.row))commands.set(command.row,command);
    const end=Math.min(rows.length,startPreviewPage*50);
    for(let index=(startPreviewPage-1)*50;index<end;index++){
      const row=rows[index],skip=index<first,command=commands.get(row.Row),tr=element('tr',skip?'sequence-row-unstarted':'');
      tr.dataset.sequencePreviewRow=row.Row;
      for(const text of [row.Row+(skip?'（跳过）':''),row.Keyword,row.Description,command?[row.Command,command.name,command.action+' → '+command.destinations.join('；')].join('\n'):row.Command,row.Condition,row.Mark,row.Expected||((data.definition.Mode==='execute'&&!['TEXT','PLUSE','PAUSE'].includes(row.Keyword.toUpperCase()))?'1（默认）':'立即'),['TEXT','PLUSE','PAUSE'].includes((row.Keyword||'').trim().toUpperCase())?'—':row.Timeout||String(config.defaultTimeout)+'（默认）']){const cell=td(text);cell.title=String(text||'');tr.append(cell);}body.append(tr);
    }
    $('sequence-start-preview-page').textContent=startPreviewPage+' / '+pages+' · 共'+rows.length+'行';
    $('sequence-start-preview-prev').disabled=startPreviewLoading||startPreviewPage<=1;$('sequence-start-preview-next').disabled=startPreviewLoading||startPreviewPage>=pages;
    if(!rows.length)empty(body,8,startPreviewLoading?'正在读取…':data?'当前序列没有步骤':'预览读取失败');
    if(data?.errors.length)startError('配置错误：'+data.errors.join('；'));
    $('sequence-start-confirm').title=app.state?.mode!=='live'?'请先开始监听':data?.warnings.join('；')||'';
    $('sequence-start-confirm').disabled=busy||startPreviewLoading||!data||!!data.errors.length||app.state?.mode!=='live';
  }
  $('sequence-start-preview-prev').addEventListener('click',()=>{startPreviewPage--;drawStartPreview();});
  $('sequence-start-preview-next').addEventListener('click',()=>{startPreviewPage++;drawStartPreview();});
  $('sequence-start-row').addEventListener('input',()=>{const index=startPreviewData?.definition.Rows.findIndex(r=>r.Row===Number($('sequence-start-row').value));if(index>=0)startPreviewPage=Math.floor(index/50)+1;drawStartPreview();});
  let config=null,preview=null,previewDefinition=null,previewPage=1,runPage=1,historyPage=1,runtime=null,busy=false,loading=false,generation=0,lastRefresh=0,lastHistory=0,previewEpoch=0,configDirty=false;
  let runMode='execute';const runSelections={judge:'',execute:''};
  const groupEdits=new Map();
  const compareGroup=(a,b)=>a.localeCompare(b,'zh-CN',{numeric:true});
  const previewGroup=definition=>groupEdits.get(definition?.Id)??definition?.Group??'';
  let taskRows=[],taskSession='',activeTask='',taskDrawSignature='',pendingCalledTask='',taskPolling=false,taskPollRequest=0,taskSelectionRevision=0,taskEntryPending=false,historyFocused=false;
  const closedTasks=new Set(),taskPages=new Map(),taskFocusPages=new Map();
  const modeName=mode=>mode==='execute'?'序列执行':'序列判读';
  function runViewActive(){return ['sequence-run','sequence-execute'].includes(app.view);}
  function taskKey(){return 'sequence-tabs:'+app.user?.username+':'+taskSession;}
  function saveTaskTabs(){try{sessionStorage.setItem(taskKey(),JSON.stringify({selected:activeTask,closed:[...closedTasks]}));}catch{}}
  function groupedSequenceOptions(rows,includeMode=false){
    const groups=new Map(),ungrouped=[];
    for(const definition of rows){
      const label=(definition.Code?definition.Code+' · ':'')+definition.Name+(includeMode?' · '+modeName(definition.Mode):'');
      const option=new Option(label,definition.Id);
      if(!definition.Group){ungrouped.push(option);continue;}
      let group=groups.get(definition.Group);
      if(!group){group=element('optgroup');group.label=definition.Group;groups.set(definition.Group,group);}
      group.append(option);
    }
    if(!groups.size)return ungrouped;
    const options=[...groups.values()].sort((a,b)=>compareGroup(a.label,b.label));
    if(ungrouped.length){const group=element('optgroup');group.label='（未分组）';group.append(...ungrouped);options.push(group);}
    return options;
  }
  function fillRunOptions(){
    if(!config)return;const mode=$('sequence-launch-mode').value,selected=runSelections[mode]||$('sequence-run-select').value,rows=config.definitions.filter(d=>d.Mode===mode);
    $('sequence-run-select').replaceChildren(...groupedSequenceOptions(rows));
    if(rows.some(d=>d.Id===selected))$('sequence-run-select').value=selected;
    syncStartSettings(rows.find(d=>d.Id===$('sequence-run-select').value));
  }
  function activateRunMode(){
    if(app.view==='sequence-execute'){$('sequence-launch-mode').value='execute';navigate('sequence-run',true);}
  }
  function selectTask(id,preserveCall=false,showHistory=false){
    if(!preserveCall)pendingCalledTask='';
    if(id&&!taskRows.some(r=>r.runId===id))return;
    if(activeTask)taskPages.set(activeTask,{runPage,historyPage,result:$('sequence-history-result').value});
    activeTask=id;historyFocused=!id&&showHistory;closedTasks.delete(id);taskSelectionRevision++;loading=false;historyRequest++;runListRequest++;runListLoading=false;if(!id)nextRunList=0;
    const selected=taskRows.find(r=>r.runId===id);runtime=selected||null;runMode=selected?.mode||$('sequence-launch-mode').value;
    const position=taskPages.get(id);runPage=position?.runPage||1;historyPage=position?.historyPage||1;historySearch='';$('sequence-history-search').value='';
    $('sequence-history-result').value=position?.result||'all';$('sequence-history-scope').value=id?'current':'all';
    for(const field of ['sequence-history-search','sequence-history-query','sequence-history-scope'])$(field).classList.toggle('hidden',!!id);historyFilters.querySelector('.current-table-pagination').classList.toggle('hidden',!!id);historyRuns.classList.toggle('hidden',!!id);taskHistory.querySelector('h2').textContent=id?'本次任务结果与异常记录':'历史运行结果与异常记录';taskHistory.querySelector('.panel-heading p').textContent=id?'仅显示本次任务的步骤、结果和异常，可筛选异常并填写原因后确认。':'按序列号、名称、日期或运行编号检索，点击历史运行的“查看记录”查看该次结果；不会恢复执行。';
    $('sequence-history-filter-error').textContent='';$('sequence-runs-page').textContent='';
    $('sequence-run-error').textContent='';empty($('sequence-run-rows'),10,'正在读取本次运行');empty($('sequence-history-rows'),9,'正在读取记录');
    $('sequence-summary').replaceChildren();$('sequence-t0').replaceChildren();$('sequence-run-heading').textContent=selected?selected.name:'运行详情';
    launchPanel.classList.toggle('hidden',!!id||historyFocused);detailPanel.classList.toggle('hidden',!id);saveTaskTabs();drawTaskTabs();controls();
    lastRefresh=lastHistory=0;if(id)refreshRun();history();
  }
  function drawTaskTabs(){
    const signature=JSON.stringify([activeTask,historyFocused,[...closedTasks],taskRows.map(r=>[r.runId,r.name,r.mode,r.running,r.paused,r.reason,r.abnormal])]);if(signature===taskDrawSignature)return;taskDrawSignature=signature;
    const first=element('button','sequence-task-tab'+(!activeTask&&!historyFocused?' active':''),'启动页面');first.type='button';first.setAttribute('aria-pressed',String(!activeTask&&!historyFocused));first.addEventListener('click',()=>selectTask(''));
    const historyTab=element('button','sequence-task-tab'+(!activeTask&&historyFocused?' active':''),'历史运行');historyTab.id='sequence-history-tab';historyTab.type='button';historyTab.setAttribute('aria-pressed',String(!activeTask&&historyFocused));historyTab.addEventListener('click',()=>{selectTask('',false,true);nextRunList=0;loadRuns(true);});
    const nodes=[first,historyTab];for(const task of taskRows.filter(r=>!closedTasks.has(r.runId))){
      const wrapper=element('span','sequence-task-tab-wrap'),button=element('button','sequence-task-tab'+(activeTask===task.runId?' active':''));button.type='button';button.dataset.runId=task.runId;
      button.title=modeName(task.mode)+' / '+task.name+' / '+task.startedAt+' / '+task.runId;
      button.setAttribute('aria-pressed',String(activeTask===task.runId));button.append(element('span','sequence-task-dot '+(task.running?'running':task.abnormal?'failed':task.reason.includes('通过')?'ended':'stopped')),element('span','sequence-task-name',task.name),element('small','',task.mode==='execute'?'执行':'判读'));
      button.addEventListener('click',()=>selectTask(task.runId));wrapper.append(button);
      if(!task.running){const close=element('button','sequence-task-close','×');close.type='button';close.title='关闭已结束页签，历史结果与异常记录保留';close.setAttribute('aria-label','关闭页签 '+task.name);close.addEventListener('click',()=>{closedTasks.add(task.runId);if(activeTask===task.runId)selectTask('');else{drawTaskTabs();saveTaskTabs();}});wrapper.append(close);}nodes.push(wrapper);
    }runTabs.replaceChildren(...nodes);
    const body=$('sequence-task-list'),runningTasks=taskRows.filter(r=>r.running);body.replaceChildren();for(const task of [...runningTasks].reverse()){
      const tr=element('tr');for(const text of [task.name+'\n'+task.runId.slice(0,8),modeName(task.mode)+' / '+task.code,task.parentRunId?'CALL · 来源 '+(taskRows.find(r=>r.runId===task.parentRunId)?.name||task.parentRunId.slice(0,8)):'手动启动',task.startedAt,task.paused?'已暂停':task.running?'运行中':task.reason])tr.append(td(text));
      const action=td(''),open=element('button','button small','查看运行');open.type='button';open.addEventListener('click',()=>selectTask(task.runId));action.append(open);tr.append(action);body.append(tr);
    }if(!runningTasks.length)empty(body,6,'当前没有运行中的任务。历史运行请在下方序列结果与异常记录中查询。');
  }
  async function pollLaunch(){
    if(taskPolling||!app.user)return;taskPolling=true;const epoch=generation,request=++taskPollRequest,selection=taskSelectionRevision;
    try{const value=await api('sequence/tasks',{activeOnly:true,retain:taskEntryPending?[]:taskRows.filter(r=>!closedTasks.has(r.runId)).map(r=>r.runId)});if(epoch!==generation||request!==taskPollRequest)return;
      const old=new Set(taskRows.map(r=>r.runId)),first=taskSession!==value.session,opening=first||taskEntryPending;taskEntryPending=false;
      if(first){taskSession=value.session;closedTasks.clear();let saved;try{saved=JSON.parse(sessionStorage.getItem(taskKey())||'null');}catch{}if(saved){for(const id of saved.closed||[])closedTasks.add(id);activeTask=saved.selected||'';}}
      taskRows=value.rows;for(const task of taskRows){if(task.running)closedTasks.delete(task.runId);else if(opening||!old.has(task.runId))closedTasks.add(task.runId);}
      const called=first?null:[...taskRows].reverse().find(r=>r.running&&r.parentRunId&&!old.has(r.runId));if(called)pendingCalledTask=called.runId;
      if(activeTask&&!taskRows.some(r=>r.runId===activeTask&&(!opening||r.running)))activeTask='';
      drawTaskTabs();controls();
      if(opening){const selected=activeTask;activeTask='';selectTask(selected);}
      else if(pendingCalledTask&&!busy&&selection===taskSelectionRevision&&['sequence-config','sequence-run','sequence-execute'].includes(app.view)){
        const target=taskRows.find(r=>r.runId===pendingCalledTask&&r.running);pendingCalledTask='';if(target){if(app.view!=='sequence-run')navigate('sequence-run');selectTask(target.runId);toast('CALL已创建运行页签：'+target.name);}
      }
    }catch(e){if(epoch===generation)$('sequence-run-error').textContent='运行列表读取失败：'+e.message;}finally{taskPolling=false;}
  }
  function resultReason(status,reason,input){return status==='通过'&&(input?.Keyword||'').trim().toUpperCase()!=='CALL'?'':reason||'';}
  const seconds=value=>value==null?'—':Number(value).toFixed(3)+'s';
  const shipTimeText=ship=>ship?.Status==='有效'?ship.Value:'—（'+(ship?.Status||'未记录')+'）';
  function sequenceRowClass(row){
    if(row.abnormal||['异常','超时'].includes(row.status))return 'sequence-row-error';
    if(['通过','注释','T0已建立'].includes(row.status))return 'sequence-row-pass';
    if(['未开始','已跳过','记录不完整'].includes(row.status)||(row.status==='终止'&&row.actual==null))return 'sequence-row-unstarted';
    return 'sequence-row-active';
  }
  const dirtySheets=new Set();
  function td(value){return element('td','',value??'');}
  function empty(body,columns,message){const tr=element('tr');const cell=td(message);cell.colSpan=columns;tr.append(cell);body.replaceChildren(tr);}
  function hint(){const execute=$('sequence-mode').value==='execute';$('sequence-mode-hint').textContent=execute?'主动发送：期望执行时间空白默认1秒，相对间隔最短0.2秒；T0:n从本序列T0计时，F:n从本序列开始计时，实际开始也遵守最短间隔。异常或超时终止，暂停时冻结计时。':'仅监视：按标志和期望执行时间调度；数字为上一项完成后等待的秒数，T0:n为本序列T0后的秒数，F:n为本序列启动后的秒数。空白立即开始，到时才开始判读或指令监视，超时从实际开始后计算。';}
  function controls(){
    importCancel.disabled=(busy&&!importReading)||!(importReading||preview||$('sequence-file').files.length);
    importClear.disabled=busy&&!importReading;
    for(const id of ['sequence-start-close','sequence-start-cancel','sequence-start-confirm','sequence-run-name','sequence-start-row','sequence-start-t0','sequence-start-f0'])$(id).disabled=busy;
    const active=!!activeTask&&!!runtime?.running,configActive=false;for(const id of ['sequence-mode','sequence-timeout','sequence-source','sequence-file','sequence-read','sequence-import-policy','sequence-import-group','sequence-settings-save','sequence-delete','sequence-apply'])$(id).disabled=busy||configActive;
    $('sequence-convert').disabled=busy||configActive;$('sequence-convert-export').disabled=busy||dirtySheets.size>0||!preview;
    const editing=!!preview?.editId;for(const id of ['sequence-save-edits','sequence-cancel-edits'])$(id).classList.toggle('hidden',!editing);
    $('sequence-preview-title').textContent=editing?'已入库序列修改':'读取后预览与修改';
    $('sequence-save-edits').disabled=busy||configActive||!editing||dirtySheets.size>0||preview.definitions.some(item=>item.errors.length);$('sequence-cancel-edits').disabled=busy||configActive||!editing;
    $('sequence-recheck').disabled=busy||configActive||!preview||!previewDefinition;$('sequence-preview-name').disabled=busy||configActive||!preview||!previewDefinition;
    $('sequence-preview-group').disabled=busy||configActive||!previewDefinition;
    appendRow.disabled=busy||!preview||!previewDefinition||(previewDefinition.Rows?.length||0)>=5000;
    for(const input of $('sequence-preview-rows').querySelectorAll('textarea'))input.disabled=busy||configActive;
    for(const button of $('sequence-preview-rows').querySelectorAll('button'))button.disabled=busy||button.dataset.insert==='true'&&previewDefinition.Rows.length>=5000;
    const sheets=[...$('sequence-import-sheets').querySelectorAll('.sequence-sheet-check:not(:disabled)')];
    const checked=sheets.filter(sheet=>sheet.checked).length;
    selectBatchLabel.classList.toggle('hidden',editing||sheets.length<2);
    selectBatch.disabled=busy||configActive;selectBatch.checked=sheets.length>0&&checked===sheets.length;
    selectBatch.indeterminate=checked>0&&checked<sheets.length;
    $('sequence-apply').disabled=busy||configActive||editing||dirtySheets.size>0||!preview||!checked;
    $('sequence-delete').disabled=busy||configActive||!$('sequence-config-select').value;
    $('sequence-start').disabled=busy||!$('sequence-run-select').value||!!config?.error;
    if(startDialog.open)$('sequence-start-confirm').disabled=busy||startPreviewLoading||!startPreviewData||!!startPreviewData.errors.length||app.state?.mode!=='live';
    $('sequence-stop').disabled=busy||!active;$('sequence-pause').disabled=busy||!active||runtime.paused||runtime.mode!=='execute';$('sequence-resume').disabled=busy||!active||!runtime.paused;
    const selected=config?.definitions.find(d=>d.Id===$('sequence-run-select').value);$('sequence-start').textContent=selected?'预览并启动'+modeName(selected.Mode):'预览并启动序列';
    $('sequence-send-note').textContent=selected?.Mode==='execute'?'本序列按已保存步骤发送：空白期望时间默认1秒，最短间隔0.2秒；并行段内部仍只监视指令。':'本序列不主动发送指令；按标志和期望执行时间等待，到时才开始判读或监视，空白立即开始，判据超时从实际开始计算。';
  }
  selectBatch.addEventListener('change',()=>{
    for(const sheet of $('sequence-import-sheets').querySelectorAll('.sequence-sheet-check:not(:disabled)'))sheet.checked=selectBatch.checked;
    controls();
  });
  function revokeSequencePreview(token){if(token)api('sequence/preview/cancel',{token}).catch(()=>{});}
  function releaseSequencePreview(){revokeSequencePreview(importToken||preview?.token);importToken='';}
  function resetSequenceImport(cancelled){
    if(busy&&!importReading)return;if(importReading){importReading=false;busy=false;}
    $('sequence-file').value='';$('sequence-import-error').textContent='';$('sequence-conversion-report').replaceChildren();invalidate();previewPage=1;drawPreview();
    $('sequence-validation').textContent=cancelled?'已取消读取，已保存序列未修改。':'已清空本次读取，已保存序列未修改。';toast(cancelled?'已取消序列读取与转换':'已清空序列读取与转换结果');
  }
  importCancel.addEventListener('click',()=>resetSequenceImport(true));
  function invalidate(){if(importReading){importReading=false;busy=false;}releaseSequencePreview();importProgress.textContent='';$('sequence-preview-name').value='';$('sequence-preview-group').value='';$('sequence-conversion-panel').classList.add('hidden');dirtySheets.clear();groupEdits.clear();preview=null;previewDefinition=null;previewEpoch++;$('sequence-import-sheets').replaceChildren();$('sequence-validation').textContent='配置已改变，请重新读取或检查已保存序列。';empty($('sequence-preview-rows'),8,'等待读取');controls();}
  function drawConfig(value){
    config=value;const configId=$('sequence-config-select').value;runSelections[$('sequence-launch-mode').value]=$('sequence-run-select').value||runSelections[$('sequence-launch-mode').value];
    $('sequence-config-select').replaceChildren(new Option('新建导入',''),...groupedSequenceOptions(value.definitions,true));
    $('sequence-config-select').value=value.definitions.some(d=>d.Id===configId)?configId:'';
    const groupNames=[...new Set(value.definitions.map(d=>d.Group).filter(Boolean))].sort(compareGroup);
    $('sequence-group-names').replaceChildren(...groupNames.map(name=>new Option(name,name)));
    fillRunOptions();
    $('sequence-source').replaceChildren(new Option('按SPP来源自动绑定',''),...value.sourceIPs.map(ip=>new Option(ip,ip)));
    $('sequence-timeout').value=value.defaultTimeout;$('sequence-config-error').textContent=value.error||'';configDirty=false;hint();controls();
  }
  async function load(){
    activateRunMode();if(runViewActive())taskEntryPending=true;const epoch=generation,dirty=configDirty,edit={mode:$('sequence-mode').value,source:$('sequence-source').value,timeout:$('sequence-timeout').value};try{const data=await api('sequence/config');if(epoch!==generation)return;drawConfig(data);if(dirty){$('sequence-mode').value=edit.mode;$('sequence-source').value=edit.source;$('sequence-timeout').value=edit.timeout;configDirty=true;hint();controls();}await pollLaunch();if(runViewActive()){if(activeTask)await refreshRun();await history();}else if($('sequence-config-select').value&&!dirty&&!preview)await savedDefinition();}catch(e){if(epoch===generation)$('sequence-config-error').textContent=e.message;}
  }
  function validation(item){
    const panel=$('sequence-validation');panel.replaceChildren();
    panel.append(element('p',item.errors.length?'error-text':'muted',item.errors.length?'检查发现 '+item.errors.length+' 项错误，不能运行。':'结构与引用检查通过；启动时仍会检查监听、解码启用和发送配置。'));
    for(const message of item.errors){panel.append(element('p','error-text',message));}for(const message of item.warnings)panel.append(element('p','muted',message));
    const list=element('div','sequence-targets');for(const command of item.commands)list.append(element('div','',`第${command.row}行 · ${command.action} ${command.code} ${command.name} → ${command.destinations.join('、')}`));if(item.commands.length)panel.append(element('h3','','指令动作与目的IP'),list);
    if(item.definition.Mode==='judge')panel.append(element('p','muted','此模式使用标志和期望执行时间；F:n从序列启动计时，T0:n从标志行全部通过后建立的T0计时，按计划时间开始判读或指令监视，TEXT同样等待填写的时间；时间留空的TEXT到达即通过。'));
  }
  function markRowEdit(){dirtySheets.add(previewDefinition.Id);$('sequence-import-error').textContent='预览已修改，请重新检测当前工作表；检测通过后保存才会更新配置。';$('sequence-validation').textContent='行内容或结构已修改，请重新检测PAR/CHANGE配对、CALL位置、参数条件和时间设置。';}
  function insertRow(index){
    if(busy||!preview||!previewDefinition)return;const rows=previewDefinition.Rows;if(rows.length>=5000){toast('单个序列最多5000行',true);return;}
    const number=Math.max(previewDefinition.NextRowNumber||1,...rows.map(r=>r.Row+1));if(!Number.isSafeInteger(number)||number>=2147483647){toast('新增行编号已超出范围，请重新导入序列',true);return;}
    const row={Row:number,Added:true,Keyword:'',Description:'',Command:'',Condition:'',Mark:'',Expected:'',Timeout:'',CommandNote:'',CellErrors:{}};
    previewDefinition.NextRowNumber=number+1;rows.splice(index,0,row);previewPage=Math.floor(index/50)+1;markRowEdit();drawPreview();controls();$('sequence-preview-rows').querySelector(`textarea[data-row="${number}"][data-field="Description"]`)?.focus();
  }
  function deleteRow(row){if(busy||!preview||!previewDefinition)return;const index=previewDefinition.Rows.indexOf(row);if(index<0)return;previewDefinition.NextRowNumber=Math.max(previewDefinition.NextRowNumber||1,...previewDefinition.Rows.map(r=>r.Row+1));previewDefinition.Rows.splice(index,1);markRowEdit();drawPreview();controls();}
  appendRow.addEventListener('click',()=>insertRow(previewDefinition?.Rows.length||0));
  function drawPreview(){$('sequence-preview-name').value=previewDefinition?.Name||'';$('sequence-preview-group').value=previewGroup(previewDefinition);
    const rows=previewDefinition?.Rows||[],pages=Math.max(1,Math.ceil(rows.length/50));previewPage=Math.max(1,Math.min(previewPage,pages));const body=$('sequence-preview-rows');body.replaceChildren();
    const editable=!!preview?.definitions.some(item=>item.definition.Id===previewDefinition?.Id);
    for(const r of rows.slice((previewPage-1)*50,previewPage*50)){
      const tr=element('tr');tr.dataset.previewRow=r.Row;tr.append(td((rows.indexOf(r)+1)+'\n'+(r.Added?'新增行号 ':'原表行号 ')+r.Row));tr.firstElementChild.className='sequence-preview-row-number';
      const fields=['Keyword','Description','Command','Condition','Mark','Expected','Timeout'];
      fields.forEach((field,column)=>{
        const cell=element('td');
        if(editable){
          const input=element('textarea','sequence-cell-editor');input.rows=2;input.value=r[field]||'';input.spellcheck=false;input.dataset.row=r.Row;input.dataset.field=field;input.setAttribute('aria-label','第'+r.Row+'行 '+['关键字','测试工作程序内容及要求','指令','判读要求','标志','期望执行时间','超时时间'][column]);input.style.cssText='box-sizing:border-box;width:100%;min-width:'+([1,3].includes(column)?'210':'100')+'px;resize:vertical';
          if(field==='Expected')input.placeholder=previewDefinition.Mode==='execute'?'1 / T0:2 / F:2；空白1秒':'1 / T0:2 / F:2；空白立即';
          input.disabled=busy;input.addEventListener('input',()=>{r[field]=input.value;dirtySheets.add(previewDefinition.Id);$('sequence-import-error').textContent='预览已修改，请点击“重新检测当前工作表”；全部修改检测通过后才能保存。';$('sequence-validation').textContent='当前内容已修改，原检测结果已过期，请重新检测。';controls();});cell.append(input);
        }else cell.textContent=field==='Expected'&&previewDefinition?.Mode==='execute'&&!['TEXT','PAUSE','PLUSE'].includes((r.Keyword||'').trim().toUpperCase())&&!String(r.Expected||'').trim()?'1（默认，秒）':r[field]||'';
        tr.append(cell);
      });const actions=td('');actions.className='sequence-row-actions';if(editable){for(const [text,action,insert] of [['上方插入',()=>insertRow(rows.indexOf(r)),true],['下方插入',()=>insertRow(rows.indexOf(r)+1),true],['删除',()=>deleteRow(r),false]]){const button=element('button','button small'+(insert?'':' danger'),text);button.type='button';button.dataset.insert=String(insert);button.setAttribute('aria-label','第'+r.Row+'行'+text);button.disabled=busy||insert&&rows.length>=5000;button.addEventListener('click',action);actions.append(button);}}tr.append(actions);body.append(tr);
    }
    if(!rows.length)empty(body,9,editable?'当前没有行，可点击“末尾添加一行”；至少保留一条有效行才能保存。':'等待读取');$('sequence-preview-page').textContent=`${previewPage} / ${pages} · ${rows.length}项`;$('sequence-preview-prev').disabled=previewPage===1;$('sequence-preview-next').disabled=previewPage===pages;
  }
  async function savedDefinition(){
    if(importReading){importReading=false;busy=false;}releaseSequencePreview();importProgress.textContent='';
    const id=$('sequence-config-select').value,epoch=++previewEpoch;preview=null;previewDefinition=null;dirtySheets.clear();groupEdits.clear();$('sequence-import-sheets').replaceChildren();$('sequence-import-error').textContent='';drawPreview();controls();
    if(!id)return;
    try{const result=await api('sequence/edit-preview',{id,revision:config.revision});if(epoch!==previewEpoch)return;
      const definition=result.definitions[0].definition;$('sequence-mode').value=definition.Mode;$('sequence-source').value=definition.SourceIP||'';configDirty=false;hint();previewPage=1;drawImport(result);
    }catch(e){if(epoch===previewEpoch)$('sequence-config-error').textContent=e.message;}
  }
  async function saveEdits(){
    if(busy||!preview?.editId||dirtySheets.size)return;busy=true;controls();const epoch=generation,id=preview.editId;
    try{const value=await api('sequence/save-edits',{token:preview.token,group:groupEdits.get(id)});if(epoch!==generation)return;invalidate();drawConfig(value);$('sequence-config-select').value=id;await savedDefinition();toast('序列修改已保存');}
    catch(error){if(epoch===generation)$('sequence-import-error').textContent='保存修改失败：'+error.message;}
    finally{if(epoch===generation){busy=false;controls();}}
  }
  $('sequence-save-edits').addEventListener('click',saveEdits);
  $('sequence-cancel-edits').addEventListener('click',()=>{dirtySheets.clear();savedDefinition();});
  async function sequenceUpload(file,query){
    let bytes;
    try{bytes=await file.arrayBuffer();}
    catch(error){throw new Error('本地文件读取失败：文件可能在选择后被修改、移动，或当前无法读取。请保存Excel后重新选择文件。'+(error.name?'（'+error.name+'）':''));}
    const controller=new AbortController(),timer=setTimeout(()=>controller.abort(),60000);
    try{
      let response;
      try{response=await fetch('/api/sequence/preview?'+query,{method:'POST',credentials:'same-origin',headers:{'Content-Type':'application/octet-stream','X-CSRF-Token':app.user.csrf},body:bytes,signal:controller.signal});}
      catch(error){
        if(error.name==='AbortError')throw new Error('上传或服务器处理超过60秒，请检查服务器是否运行后重试；本次未保存序列。');
        const probe=new AbortController(),probeTimer=setTimeout(()=>probe.abort(),3000);
        try{
          const status=await fetch('/api/auth/me',{credentials:'same-origin',cache:'no-store',signal:probe.signal});
          if(!status.ok)throw new Error('probe');
        }catch(unavailable){throw new Error('无法连接服务器，上传请求未完成。请检查程序是否运行、当前网页地址和网络连接，再重新选择文件重试。');}
        finally{clearTimeout(probeTimer);}
        throw new Error('上传请求未完成，服务器仍可访问。请重新选择文件后重试；浏览器未提供更详细的网络失败原因。');
      }
      let result;
      try{result=await response.json();}
      catch(error){throw new Error('服务器未返回完整的序列检查结果（HTTP '+response.status+'），请查看服务器运行日志后重试。');}
      if(!response.ok)throw Object.assign(new Error(result.error || '服务器拒绝读取（HTTP '+response.status+'）'),{status:response.status});
      if(!Array.isArray(result.definitions))throw new Error('服务器返回的序列检查结果格式不完整，请刷新页面后重试。');
      return result;
    }finally{clearTimeout(timer);}
  }
  async function read(convert=false){
    if(busy)return;
    const files=[...$('sequence-file').files];$('sequence-import-error').textContent='';
    if(!files.length){$('sequence-import-error').textContent='读取失败：请选择.xlsx序列文件。';return;}
    if(convert&&files.length>1){$('sequence-import-error').textContent='转换小工具每次处理一份Excel。请只选择一份文件后再转换。';return;}
    invalidate();busy=true;importReading=true;const epoch=generation,request=previewEpoch;controls();
    const stillReading=()=>epoch===generation&&request===previewEpoch;
    const result={token:'',definitions:[],conversion:[],fileErrors:[],fileCount:files.length,passedFiles:0};
    const settings=new URLSearchParams({mode:$('sequence-mode').value,sourceIP:$('sequence-source').value,timeout:$('sequence-timeout').value,group:$('sequence-import-group').value});
    if(convert)settings.set('convert','commands');
    if(files.length>1)settings.set('batch','1');
    try{
      for(const [index,file] of files.entries()){
        importProgress.textContent=`正在读取 ${index+1} / ${files.length} · ${file.name}`;
        try{
          if(!/\.xlsx$/i.test(file.name))throw new Error('文件格式不支持，请选择.xlsx序列文件。');
          if(!file.size)throw new Error('文件内容为空，请重新选择有效的.xlsx文件。');
          if(file.size>8*1024*1024)throw new Error('单个文件最大8MB，请减小文件后重试。');
          const query=new URLSearchParams(settings);query.set('filename',file.name);
          if(result.token)query.set('batchToken',result.token);
          const workbook=await sequenceUpload(file,query);
          if(!stillReading()){revokeSequencePreview(workbook.token);return;}
          result.token=importToken=workbook.token;
          result.definitions.push(...workbook.definitions);result.conversion.push(...(workbook.conversion||[]));result.passedFiles++;
        }catch(error){
          if(!stillReading())return;
          if(result.token&&error.status===409)throw error;
          result.fileErrors.push(file.name+'：读取失败。'+error.message);
        }
      }
      importProgress.textContent=files.length>1?`已读取 ${result.passedFiles} / ${files.length} 份 Excel · ${result.definitions.length} 条序列`:'';
      if(!result.passedFiles){$('sequence-import-error').textContent=result.fileErrors.join('\n');return;}
      importToken='';drawImport(result);
      toast(result.fileErrors.length?'已读取可用文件，请查看失败原因':result.conversion.some(record=>record.warning)?'已读取，存在需人工核对的转换项，请查看转换报告':result.definitions.some(item=>item.errors.length)?'已读取，请在预览中修改错误后重新检测':'已读取预览，可修改并重新检测');
    }catch(error){
      if(stillReading()){releaseSequencePreview();importProgress.textContent='';$('sequence-import-error').textContent=error.message;}
    }finally{if(stillReading()){importReading=false;busy=false;controls();}}
  }
  function drawImport(result){
    const previousId=previewDefinition?.Id,selected=new Map([...document.querySelectorAll('.sequence-sheet-check')].map(c=>[c.value,c.checked]));
    const sequenceNames=new Map(result.definitions.map(item=>[item.definition.Sheet,item.definition.Name]));const conversion=result.conversion||[];$('sequence-conversion-panel').classList.toggle('hidden',!conversion.length);
    $('sequence-conversion-report').replaceChildren(...conversion.map(record=>element('p',record.warning?'small-text error-text':'small-text',(sequenceNames.get(record.sheet)||record.sheet)+' 第'+record.row+'行 · '+record.status+' · '+(record.before||'（空）')+' → '+(record.after||'（空）')+(record.keywordBefore!==record.keywordAfter?' · 关键字 '+(record.keywordBefore||'（空）')+' → '+(record.keywordAfter||'（空）'):'')+(record.markBefore!==record.markAfter?' · 标志 '+(record.markBefore||'（空）')+' → '+(record.markAfter||'（空）'):'')+(record.reason?' · '+record.reason:''))));
    if(conversion.some(record=>record.warning))$('sequence-conversion-panel').open=true;
    preview=result;const container=$('sequence-import-sheets');container.replaceChildren();const failures=[];
    let currentFilename='';
    for(const item of result.definitions){
      const filename=item.definition.Filename;
      if(result.fileCount>1&&filename!==currentFilename){container.append(element('h3','sequence-import-source',filename));currentFilename=filename;}
      const line=element('div','sequence-sheet-line'),label=element('label'),check=element('input');check.type='checkbox';check.className='sequence-sheet-check';check.value=item.definition.Id;check.checked=item.errors.length===0&&(selected.get(item.definition.Id)??true);check.disabled=item.errors.length>0||dirtySheets.has(item.definition.Id);check.addEventListener('change',controls);
      const group=element('span','muted small-text',previewGroup(item.definition).trim()||'未分组');group.id='sequence-sheet-group-'+item.definition.Id;
      label.append(check,document.createTextNode(item.definition.Name+' · '+(item.definition.Code||'保存时分配序列号')+' · '+item.definition.Rows.length+'项 · '+item.errors.length+'项错误'+(dirtySheets.has(item.definition.Id)?' · 待重新检测':'')+' · '),group);
      const inspect=element('button','button small','查看 / 修改');inspect.type='button';inspect.addEventListener('click',()=>{previewDefinition=item.definition;previewPage=1;validation(item);drawPreview();controls();});line.append(label,inspect);container.append(line);
      for(const error of item.errors)failures.push('序列“'+item.definition.Name+'” '+error);
    }
    const current=result.definitions.find(item=>item.definition.Id===previousId)||result.definitions[0];previewDefinition=current?.definition||null;
    if(current)validation(current);drawPreview();
    $('sequence-import-error').textContent=[...(result.fileErrors||[]),...(dirtySheets.size?['还有修改尚未重新检测。']:[]),...(failures.length?['已读取，发现'+failures.length+'项配置错误；请在预览中修改后重新检测。',...failures]:[])].join('\n');controls();
  }
  async function recheck(){
    if(busy||!preview||!previewDefinition)return;const epoch=generation,request=previewEpoch,id=previewDefinition.Id;
    busy=true;controls();
    try{
      const result=await api('sequence/recheck',{token:preview.token,id,name:previewDefinition.Name,rows:previewDefinition.Rows,nextRowNumber:previewDefinition.NextRowNumber||undefined});if(epoch!==generation||request!==previewEpoch)return;
      result.fileErrors=preview.fileErrors;result.fileCount=preview.fileCount;
      for(const item of preview.definitions)if(item.definition.Id!==id&&dirtySheets.has(item.definition.Id)){const index=result.definitions.findIndex(d=>d.definition.Id===item.definition.Id);if(index>=0)result.definitions[index]=item;}
      dirtySheets.delete(id);drawImport(result);toast('重新检测完成，请查看错误提示和工作表勾选状态');
    }catch(error){if(epoch===generation)$('sequence-import-error').textContent='重新检测失败：'+error.message;}
    finally{if(epoch===generation){busy=false;controls();}}
  }
  $('sequence-preview-name').addEventListener('input',()=>{
    if(!preview||!previewDefinition||busy)return;previewDefinition.Name=$('sequence-preview-name').value;dirtySheets.add(previewDefinition.Id);
    $('sequence-import-error').textContent='序列名称已修改，请重新检测当前工作表后保存。';controls();
  });
  $('sequence-preview-group').addEventListener('input',event=>{
    if(!previewDefinition||busy)return;
    const {Id,Group}=previewDefinition,value=event.target.value.trim();
    if(value===(Group||''))groupEdits.delete(Id);else groupEdits.set(Id,value);
    $('sequence-sheet-group-'+Id).textContent=value||'未分组';
  });
  $('sequence-recheck').addEventListener('click',recheck);
  $('sequence-convert-export').addEventListener('click',async()=>{
    if(busy||!preview||dirtySheets.size)return;busy=true;controls();const epoch=generation;
    try{
      const response=await fetch('/api/sequence/export-converted',{method:'POST',credentials:'same-origin',headers:{'Content-Type':'application/json','X-CSRF-Token':app.user.csrf},body:JSON.stringify({token:preview.token})});
      if(!response.ok){const error=await response.json();throw new Error(error.error||'导出失败');}
      const blob=await response.blob();if(epoch!==generation)return;const url=URL.createObjectURL(blob),link=element('a');link.href=url;const filename=/filename\*=UTF-8''([^;]+)/i.exec(response.headers.get('Content-Disposition')||'');link.download=filename?decodeURIComponent(filename[1]):((preview.definitions[0]?.definition.Filename||'序列.xlsx').replace(/\.xlsx$/i,'')+'_判读格式匹配.xlsx');document.body.append(link);link.click();link.remove();setTimeout(()=>URL.revokeObjectURL(url),30000);
    }catch(error){if(epoch===generation)$('sequence-import-error').textContent='导出失败：'+error.message;}
    finally{if(epoch===generation){busy=false;controls();}}
  });

  async function saveImport(){
    if(busy||!preview||dirtySheets.size)return;
    const mode=$('sequence-import-policy').value;
    if(mode==='replace'&&preview.fileErrors?.length&&!confirm('部分文件读取失败。确定用当前勾选的序列替换全部已保存序列？'))return;
    busy=true;controls();const epoch=generation;
    try{
      const ids=[...document.querySelectorAll('.sequence-sheet-check:checked')].map(c=>c.value);
      const groups=Object.fromEntries(ids.filter(id=>groupEdits.has(id)).map(id=>[id,groupEdits.get(id)]));
      const value=await api('sequence/apply',{token:preview.token,ids,mode,groups});
      if(epoch!==generation)return;
      invalidate();drawConfig(value);$('sequence-config-select').value=ids[0];
      const saved=value.definitions.find(d=>d.Id===ids[0]);
      if(saved){runSelections[saved.Mode]=saved.Id;fillRunOptions();}
      await savedDefinition();toast('序列已保存');
    }
    catch(e){if(epoch===generation)$('sequence-config-error').textContent=e.message;}finally{if(epoch===generation){busy=false;controls();}}
  }
  async function saveSettings(remove=false){
    if(!config)return;const id=$('sequence-config-select').value;if(remove&&!confirm('删除所选序列配置？已保存的运行结果保留。'))return;busy=true;controls();const epoch=generation;
    try{const value=await api('sequence/config',{id,mode:$('sequence-mode').value,sourceIP:$('sequence-source').value,defaultTimeout:$('sequence-timeout').value,revision:config.revision,delete:remove?'true':'false'});if(epoch!==generation)return;invalidate();drawConfig(value);await savedDefinition();toast(remove?'序列已删除':'运行设置已保存');}
    catch(e){if(epoch===generation)$('sequence-config-error').textContent=e.message;}finally{if(epoch===generation){busy=false;controls();}}
  }
  function summary(value){
    runtime=value;const index=taskRows.findIndex(r=>r.runId===value.runId);if(index>=0)taskRows[index]=value;
    $('sequence-other-runtime').textContent='本次运行编号：'+value.runId+' · '+(value.origin||'手动启动')+(value.parentRunId?' · 来源任务：'+(taskRows.find(r=>r.runId===value.parentRunId)?.name||value.parentRunId):'');const box=$('sequence-summary');box.replaceChildren();box.append(element('strong','',(value.code?value.code+' · ':'')+(value.name||'尚未启动序列')),element('span','',value.reason||''),element('span','',`完成 ${value.completed}/${value.total} · 异常 ${value.abnormal}项`));
    if(value.mode)box.append(element('span','',`序列时间（F） ${seconds(value.elapsed)}`+(value.mode==='execute'?` · 暂停 ${seconds(value.pausedSeconds)}`:'')));
    if(value.startRow)box.append(element('span','',`从原表第${value.startRow}行开始 · 已跳过 ${value.skipped||0}项 · 本次运行 ${seconds(value.sessionElapsed)}`+(value.initialF0?' · 初始F0='+value.initialF0+'s':'')+(value.initialT0!=null?' · 初始T0='+value.initialT0+'s':'')));
    const t0=$('sequence-t0');t0.replaceChildren();
    if(value.t0==null){t0.append(element('strong','','T0：未建立'),element('span','muted','标志列填写T0：本行到达且满足期望时间并通过后建立；TEXT时间留空时到达即建立。'));}
    else{t0.append(element('strong','','T0 已建立'+(value.t0Preset?'（启动补设）':'')),element('span','',(value.t0Preset?'补设对应时刻：':'建立时刻：')+(value.t0EstablishedAt||'—')),element('span','',(value.t0Preset?'预设序列位置 ':'建立于序列第 ')+seconds(value.t0)),element('span','sequence-t0-elapsed','距T0：+'+seconds(value.t0Elapsed)));}
    $('sequence-progress').textContent=value.running?`当前原表第${value.currentRow}行${value.inParallel?' · 并行段运行中':''} · ${value.paused?'已暂停':'运行中'}`:value.reason||'尚未启动';controls();
  }
  async function refreshRun(){
    if(loading||!app.user||!activeTask)return;loading=true;const epoch=generation,mode=runMode,runId=activeTask,selection=taskSelectionRevision,requestedPage=runPage;
    try{let value=await api('sequence/state?'+new URLSearchParams({runId,page:String(requestedPage)}));if(epoch!==generation||selection!==taskSelectionRevision||runId!==activeTask||requestedPage!==runPage)return;
      const focusPage=value.runtime?.currentPage,previousFocus=taskFocusPages.get(runId);
      if(focusPage){taskFocusPages.set(runId,focusPage);if((previousFocus==null?value.runtime.running:previousFocus!==focusPage)&&focusPage!==runPage){runPage=focusPage;value=await api('sequence/state?'+new URLSearchParams({runId,page:String(focusPage)}));if(epoch!==generation||selection!==taskSelectionRevision||runId!==activeTask||focusPage!==runPage)return;}}
      summary(value.runtime);runPage=value.page;$('sequence-run-page').textContent=`${value.page} / ${value.pages} · ${value.total}项`;$('sequence-run-prev').disabled=value.page===1;$('sequence-run-next').disabled=value.page===value.pages;
      $('sequence-diagnostic-export').href='/api/sequence/diagnostic?'+new URLSearchParams({runId});
      const body=$('sequence-run-rows');body.replaceChildren();for(const r of value.rows){const tr=element('tr',sequenceRowClass(r));const input=r.input;tr.dataset.sequenceRow=input.Row;tr.dataset.status=r.status;
        const values=Object.entries(r.values).map(([key,v])=>key+' = '+v).join('\n');const timing=[r.startedAt,r.finishedAt?'结束 '+r.finishedAt:''];if(value.definition?.Mode){timing.push('计划 '+seconds(r.planned),'开始 '+seconds(r.actual),'延迟 '+seconds(r.delay));}timing.push('船时 '+shipTimeText(r.shipTime));
        const timeout=['TEXT','PAUSE','PLUSE'].includes((input.Keyword||'').trim().toUpperCase())||r.timeout==null?'—':String(r.timeout)+'s'+((input.Timeout||'').trim()?'':'（默认）');
        for(const v of [input.Row,input.Keyword,input.Description,input.Command+(r.commandName?'\n'+r.commandName:''),input.Condition,timeout,r.status+(r.remaining==null?'':'\n剩余 '+seconds(r.remaining)),values||'—',timing.filter(Boolean).join('\n'),[resultReason(r.status,r.reason,input),r.evaluation?.count?'最近判读 '+r.evaluation.lastAt+' · '+r.evaluation.result+' · '+r.evaluation.count+'次':input.Condition&&r.actual!=null?'尚未进行参数判读':''].filter(Boolean).join('\n')])tr.append(td(v));body.append(tr);}
      if(!value.rows.length)empty(body,10,'请先在序列配置中导入并保存序列');
    }catch(e){if(epoch===generation&&selection===taskSelectionRevision)$('sequence-run-error').textContent=e.message;}finally{if(selection===taskSelectionRevision)loading=false;}
  }
  async function control(action,runName='',start=null){
    if(busy)return;busy=true;controls();const epoch=generation;$('sequence-run-error').textContent='';
    try{const requestId=start?.requestId||Array.from(crypto.getRandomValues(new Uint8Array(16)),v=>v.toString(16).padStart(2,'0')).join('');const targetRun=activeTask;
      const value=await api('sequence/control',{action,runName,mode:runMode,id:start?.id||$('sequence-run-select').value,runId:targetRun,controlRevision:runtime?.controlRevision,requestId,revision:start?.revision||config?.revision||'',startRow:start?.startRow||'',t0Elapsed:start?.t0Elapsed||'',f0Elapsed:start?.f0Elapsed||''});if(epoch!==generation)return;
      const index=taskRows.findIndex(r=>r.runId===value.runId);if(index>=0)taskRows[index]=value;else taskRows.push(value);
      if(action==='start'){startDialog.close();pendingStart=null;syncStartSettings(null);syncStartSettings(config?.definitions.find(d=>d.Id===$('sequence-run-select').value));selectTask(value.runId,true);runPage=Math.floor((value.skipped||0)/50)+1;await refreshRun();}
      else if(activeTask===targetRun){summary(value);await refreshRun();}
      drawTaskTabs();nextRunList=0;await history();toast(action==='start'?'已创建运行页签：'+value.name:action==='resume'?'已继续本任务':action==='pause'?'本任务已暂停':'本任务已停止');}

    catch(e){if(epoch===generation){$('sequence-run-error').textContent=e.message;if(action==='start')startError(e.message);}}finally{if(epoch===generation){busy=false;controls();}}
  }
  let historySearch='',runListPage=1,runListPages=1,nextRunList=0,runListRequest=0,runListLoading=false,historyRequest=0;
  function historyRunId(){if(activeTask)return activeTask;const selected=$('sequence-history-scope').value;return selected==='current'?runtime?.runId||'none':selected==='all'?'':selected;}
  function openHistoricalRun(run){
    historyFocused=true;launchPanel.classList.add('hidden');detailPanel.classList.add('hidden');drawTaskTabs();
    const selector=$('sequence-history-scope');if(![...selector.options].some(option=>option.value===run.RunId))selector.append(new Option(run.StartedAt+' · '+run.Name,run.RunId));selector.value=run.RunId;historyPage=1;
    taskHistory.querySelector('h2').textContent='历史运行：'+run.Name;taskHistory.querySelector('.panel-heading p').textContent=modeName(run.Mode)+' · '+(run.Code||'旧记录无序列号')+' · '+run.StartedAt+' · 运行编号 '+run.RunId+'；仅查看已保存结果，不会重新执行。';
    history();$('sequence-history-rows').closest('.table-scroll').scrollIntoView({block:'nearest',behavior:'smooth'});
  }
  function drawHistoricalRuns(data){
    const body=$('sequence-history-run-list'),fragment=document.createDocumentFragment();
    for(const run of data.rows){const tr=element('tr');tr.dataset.historyRunId=run.RunId;for(const text of [run.Name+'\n'+(run.Code||'旧记录无序列号'),modeName(run.Mode),run.StartedAt,run.LastTime,run.Status+' / '+run.Events+'条'])tr.append(td(text));const action=td(''),button=element('button','button small','查看记录');button.type='button';button.addEventListener('click',()=>openHistoricalRun(run));action.append(button);tr.append(action);fragment.append(tr);}
    body.replaceChildren(fragment);if(!data.rows.length)empty(body,6,historySearch?'没有符合条件的历史运行，请调整名称、序列号或日期。':'尚无历史运行记录。运行结束后的结果会保留在这里。');
  }
  async function loadRuns(force=false){
    if(!app.user||activeTask||(runListLoading&&!force))return;runListLoading=true;nextRunList=Date.now()+8000;const epoch=generation,request=++runListRequest,search=historySearch,page=runListPage,mode=activeTask?runMode:'';
    try{
      const data=await api('sequence/runs?'+new URLSearchParams({page:String(page),search,mode}));if(epoch!==generation||activeTask||mode!==''||request!==runListRequest||search!==historySearch||page!==runListPage)return;
      runListPage=data.page;runListPages=data.pages;const selector=$('sequence-history-scope'),selected=selector.value,oldLabel=selector.selectedOptions[0]?.textContent||selected;
      const options=[new Option('全部匹配运行','all'),new Option('当前运行','current')];
      for(const run of data.rows)options.push(new Option(run.StartedAt+' · '+(run.Code||'旧记录无序列号')+' · '+run.Name+' · '+run.Status+' · '+run.RunId.slice(0,8),run.RunId));
      if(!options.some(option=>option.value===selected))options.push(new Option(oldLabel,selected));selector.replaceChildren(...options);selector.value=selected;
      drawHistoricalRuns(data);
      $('sequence-runs-page').textContent='共'+data.total+'次运行 · 候选列表第'+data.page+'/'+data.pages+'页';$('sequence-runs-prev').disabled=data.page<=1;$('sequence-runs-next').disabled=data.page>=data.pages;$('sequence-history-filter-error').textContent=data.error||'';
    }catch(error){if(epoch===generation&&request===runListRequest)$('sequence-history-filter-error').textContent='历史运行列表读取失败：'+error.message;}
    finally{if(request===runListRequest)runListLoading=false;}
  }
  async function queryHistory(){historySearch=$('sequence-history-search').value.trim();historyPage=runListPage=1;$('sequence-history-scope').value='all';await loadRuns(true);await history();}
  $('sequence-history-query').addEventListener('click',queryHistory);$('sequence-history-search').addEventListener('keydown',event=>{if(event.key==='Enter'){event.preventDefault();queryHistory();}});
  $('sequence-runs-prev').addEventListener('click',()=>{runListPage=Math.max(1,runListPage-1);loadRuns(true);});$('sequence-runs-next').addEventListener('click',()=>{runListPage=Math.min(runListPages,runListPage+1);loadRuns(true);});
  async function history(){
    if(!app.user)return;if(Date.now()>=nextRunList&&document.activeElement!==$('sequence-history-scope'))loadRuns();const epoch=generation,request=++historyRequest,runId=historyRunId(),search=historySearch,requestedPage=historyPage,mode=activeTask?runMode:'',result=$('sequence-history-result').value;
    $('sequence-export').href='/api/sequence/export.csv?'+new URLSearchParams({runId,search,mode,result});
    try{const value=await api('sequence/history?'+new URLSearchParams({runId,search,mode,result,page:String(requestedPage)}));if(epoch!==generation||mode!==(activeTask?runMode:'')||request!==historyRequest||runId!==historyRunId()||search!==historySearch||requestedPage!==historyPage||result!==$('sequence-history-result').value)return;historyPage=value.page;const body=$('sequence-history-rows');body.replaceChildren();
      for(const e of value.rows){
        const tr=element('tr',e.Event==='异常'?'sequence-row-error':['通过','注释','T0已建立'].includes(e.Status)?'sequence-row-pass':'');
        for(const v of [e.Time,shipTimeText(e.ShipTime),modeName(e.Mode)+(e.Code?' · '+e.Code:'')+'\n'+e.Sequence,(e.Row||'—')+' / '+e.Event,e.Status,e.Values||'—',resultReason(e.Status,e.Reason,e.Input)||e.Input?.Description||''])tr.append(td(v));
        const confirmation=td('—'),action=td(''),incident=e.Abnormal;
        if(incident){
          tr.dataset.abnormalKey=incident.key;tr.dataset.released=String(incident.released);confirmation.replaceChildren(element('span','status-tag '+(incident.released?'neutral':'bad'),incident.released?'已确认':'未确认'));
          if(incident.released)confirmation.append(element('p','small-text',incident.reason),element('p','muted small-text',incident.releasedBy+' / '+incident.releasedAt));
          else{const button=element('button','button small','填写原因并确认');button.type='button';button.addEventListener('click',()=>window.UnifiedAbnormalUI.openRelease(incident));action.append(button);}
        }
        tr.append(confirmation,action);body.append(tr);
      }
      if(!value.rows.length)empty(body,9,result==='all'?'暂无运行记录':'暂无符合条件的异常记录');$('sequence-history-page').textContent=`${value.page} / ${value.pages} · ${value.total}条`;$('sequence-history-prev').disabled=value.page===1;$('sequence-history-next').disabled=value.page===value.pages;if(value.error)$('sequence-run-error').textContent=value.error;
    }catch(e){if(epoch===generation&&request===historyRequest)$('sequence-run-error').textContent=e.message;}
  }
  $('sequence-config-select').addEventListener('change',()=>{if((dirtySheets.size||groupEdits.size)&&!confirm('当前修改尚未保存，是否放弃并切换序列？')){$('sequence-config-select').value=preview?.editId||'';return;}savedDefinition();});
  $('sequence-run-select').addEventListener('change',()=>{runSelections[$('sequence-launch-mode').value]=$('sequence-run-select').value;syncStartSettings(config?.definitions.find(d=>d.Id===$('sequence-run-select').value));controls();});
  for(const id of ['sequence-mode','sequence-source','sequence-timeout','sequence-file'])$(id).addEventListener('change',()=>{configDirty=true;invalidate();hint();});
  $('sequence-read').addEventListener('click',()=>read());$('sequence-convert').addEventListener('click',()=>read(true));$('sequence-apply').addEventListener('click',saveImport);$('sequence-settings-save').addEventListener('click',()=>saveSettings());$('sequence-delete').addEventListener('click',()=>saveSettings(true));$('sequence-reload').addEventListener('click',()=>{invalidate();load();});
  $('sequence-go-run').addEventListener('click',()=>{$('sequence-launch-mode').value=$('sequence-mode').value;fillRunOptions();navigate('sequence-run');selectTask('');});$('sequence-go-config').addEventListener('click',()=>navigate('sequence-config'));
  for(const action of ['pause','resume','stop'])$('sequence-'+action).addEventListener('click',()=>control(action));
  $('sequence-start').addEventListener('click',async()=>{
    if(busy||startDialog.open)return;
    const definition=config?.definitions.find(d=>d.Id===$('sequence-run-select').value);if(!definition)return;
    pendingStart={id:definition.Id,revision:config.revision,requestId:Array.from(crypto.getRandomValues(new Uint8Array(16)),v=>v.toString(16).padStart(2,'0')).join('')};
    syncStartSettings(definition);startError('');startPreviewData=null;startPreviewPage=1;startPreviewLoading=true;const previewRequest=++startPreviewEpoch;startDialog.showModal();drawStartPreview();$('sequence-start-cancel').focus();
    try{const data=await api('sequence/definition?'+new URLSearchParams({id:definition.Id}));if(previewRequest===startPreviewEpoch&&startDialog.open){startPreviewData=data;const index=data.definition.Rows.findIndex(r=>r.Row===Number($('sequence-start-row').value));if(index>=0)startPreviewPage=Math.floor(index/50)+1;}}
    catch(error){if(previewRequest===startPreviewEpoch)startError('启动预览读取失败：'+error.message);}
    finally{if(previewRequest===startPreviewEpoch){startPreviewLoading=false;drawStartPreview();}}
  });
  $('sequence-start-form').addEventListener('submit',event=>{event.preventDefault();if(busy||!pendingStart||startPreviewLoading||!startPreviewData||startPreviewData.errors.length)return;if(app.state?.mode!=='live'){startError('请先开始监听');return;}const name=$('sequence-run-name').value.trim();if(!name||/[\x00-\x1F\x7F]/.test(name)){startError('请填写本次序列名称，不含换行或控制字符');return;}const startRow=$('sequence-start-row').value.trim();if(startRow&&!/^[1-9]\d*$/.test(startRow)){startError('起始行须填写序列中的原表行号（正整数）');return;}control('start',name,{...pendingStart,startRow,t0Elapsed:$('sequence-start-t0').value.trim(),f0Elapsed:$('sequence-start-f0').value.trim()});});
  for(const id of ['sequence-start-close','sequence-start-cancel'])$(id).addEventListener('click',()=>{if(!busy){startDialog.close();pendingStart=null;}});
  startDialog.addEventListener('cancel',event=>{if(busy)event.preventDefault();else pendingStart=null;});
  $('sequence-preview-prev').addEventListener('click',()=>{previewPage--;drawPreview();});$('sequence-preview-next').addEventListener('click',()=>{previewPage++;drawPreview();});
  $('sequence-run-prev').addEventListener('click',()=>{runPage--;refreshRun();});$('sequence-run-next').addEventListener('click',()=>{runPage++;refreshRun();});
  $('sequence-history-prev').addEventListener('click',()=>{historyPage--;history();});$('sequence-history-next').addEventListener('click',()=>{historyPage++;history();});$('sequence-history-refresh').addEventListener('click',()=>{loadRuns(true);history();});$('sequence-history-scope').addEventListener('change',()=>{historyPage=1;if(!activeTask){taskHistory.querySelector('h2').textContent='历史运行结果与异常记录';taskHistory.querySelector('.panel-heading p').textContent='已选择：'+($('sequence-history-scope').selectedOptions[0]?.textContent||'全部匹配运行')+'；仅查看保存记录，不会重新执行。';}history();});
  $('sequence-history-result').addEventListener('change',()=>{historyPage=1;history();});
  window.addEventListener('abnormal-records-changed',()=>{if(app.user&&runViewActive())history();});
  function render(){if(!app.user)return;const now=Date.now();if(!['sequence-config','sequence-run','sequence-execute'].includes(app.view))return;activateRunMode();if(now-lastRefresh>700){lastRefresh=now;pollLaunch();if(runViewActive()&&activeTask)refreshRun();}if(runViewActive()&&now-lastHistory>4000){lastHistory=now;history();}}
  function reset(){
    importToken='';importProgress.textContent='';groupEdits.clear();$('sequence-import-group').value='';$('sequence-group-names').replaceChildren();
    taskRows=[];taskSession='';activeTask='';historyFocused=false;taskPolling=false;taskPollRequest++;taskSelectionRevision++;closedTasks.clear();taskPages.clear();taskFocusPages.clear();drawTaskTabs();launchPanel.classList.remove('hidden');detailPanel.classList.add('hidden');pendingCalledTask='';runSelections.judge=runSelections.execute='';historySearch='';runListPage=runListPages=1;nextRunList=0;runListRequest++;historyRequest++;runListLoading=false;$('sequence-history-search').value='';$('sequence-history-result').value='all';$('sequence-history-scope').replaceChildren(new Option('全部匹配运行','all'),new Option('当前运行','current'));$('sequence-runs-page').textContent='';$('sequence-history-filter-error').textContent='';$('sequence-conversion-panel').classList.add('hidden');dirtySheets.clear();$('sequence-import-error').textContent='';generation++;importReading=false;startDialog.close();pendingStart=null;syncStartSettings(null);previewEpoch++;config=preview=previewDefinition=runtime=null;busy=loading=false;runPage=previewPage=historyPage=1;lastRefresh=lastHistory=0;configDirty=false;$('sequence-file').value='';$('sequence-import-sheets').replaceChildren();$('sequence-summary').replaceChildren();$('sequence-t0').replaceChildren();$('sequence-validation').textContent='等待读取';for(const id of ['sequence-run-error','sequence-config-error'])$(id).textContent='';for(const id of ['sequence-config-select','sequence-run-select'])$(id).replaceChildren();empty($('sequence-run-rows'),10,'等待登录');empty($('sequence-history-rows'),9,'等待登录');empty($('sequence-history-run-list'),6,'等待登录');drawPreview();controls();
  }
  $('sequence-launch-mode').addEventListener('change',()=>{fillRunOptions();if(!activeTask)runMode=$('sequence-launch-mode').value;controls();});
  detailPanel.classList.add('hidden');drawTaskTabs();hint();drawPreview();controls();return{load,render,reset};
})();
