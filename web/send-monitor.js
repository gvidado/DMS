'use strict';
window.SendMonitorUI = (() => {
  names['command-config']=['指令配置','配置发送端口、发送监视范围及指令库，导入Excel、CSV、BIN及XML控制指令。'];
  const configView=element('section','view hidden');configView.id='view-command-config';$('view-overview').after(configView);
  const panel=element('section','panel send-monitor-panel');
  panel.innerHTML=`<div class="panel-heading"><div><h2>发送数据计数 <span id="send-monitor-count" class="count-chip">0</span></h2><p>监视来源列表发往目的列表的UDP，任意组合匹配即计数加1；重复发送同样计数。</p></div><span id="send-monitor-status" class="status-tag neutral">未启动</span></div><div class="section-pad"><form id="send-monitor-form"><div class="form-grid"><label>来源IP列表（本机发送IP）<input id="send-monitor-source" required placeholder="192.168.1.16,192.168.1.17"><small>支持多个IP，用逗号、分号或空格分隔；来源须属于同一块本机网卡。</small></label><label>目的IP列表<input id="send-monitor-destination" required placeholder="192.168.1.111,192.168.1.112,192.168.1.113,192.168.1.137"><small>默认.111、.112、.113、.137；最多64个，来源和目的按任意组合匹配。</small></label><label>抓包网卡<select id="send-monitor-interface"><option value="">按来源列表自动选择</option></select><small>选择实际发送网卡；本机地址之间通信时选择Npcap回环网卡。</small></label><label>目的UDP端口<input id="send-monitor-port" type="number" min="0" max="65535" value="0" required><small>0表示统计所选来源到目的列表的全部UDP目的端口。</small></label></div><div class="import-action-line"><button id="send-monitor-save" class="button" type="submit">保存待生效配置</button><span class="muted small-text">使用页面顶部“开始监听 / 停止接收”，同时控制UDP接收和发送监视。</span><button id="send-monitor-interfaces" class="text-button" type="button">刷新网卡</button></div></form><p id="send-monitor-driver" class="muted small-text">发送监视需要服务器安装Npcap抓包驱动。</p><p id="send-monitor-error" class="form-error" role="alert"></p><div class="import-action-line"><span id="send-commands-summary" class="muted small-text"></span><a id="send-commands-export" href="/api/send-monitor/commands/export" class="button small" download="observed-commands.json">下载指令配置JSON</a></div><p id="send-commands-error" class="form-error" role="alert"></p><p id="send-monitor-summary" class="muted small-text"></p><p id="send-monitor-dirty" class="muted small-text"></p><div class="import-action-line"><button id="send-monitor-refresh" class="button small">刷新最近记录</button><a id="send-monitor-export" href="/api/send-monitor/export" class="button small" download="outgoing-udp-recent-200.csv">导出最近200条CSV</a><button id="send-monitor-clear" class="text-button">清零计数和记录</button></div><div class="table-scroll send-monitor-scroll"><table class="data-table"><thead><tr><th>记录号 / 抓包时间（北京时间）</th><th>来源 → 目的</th><th>指令编号 / 名称</th><th>UDP载荷字节数</th><th>十六进制内容（前32字节）</th><th>完整原文</th></tr></thead><tbody id="send-monitor-rows"></tbody></table></div><p class="muted small-text">计数及最近200条仅保存在内存；停止接收后保留，重新开始、手动清零会重置；配置切换保留记录，关闭服务器后删除。CSV包含最近200条完整UDP载荷及抓包帧；来源/目的严格按配置匹配，不受遥测白名单或SPP解码结果影响。当前支持完整IPv4 UDP，未重组的IP分片和截断报文单独计入诊断。</p></div>`;
  $('view-overview').prepend(panel);
  const catalogPanel=element('section','panel');catalogPanel.id='command-catalog-panel';catalogPanel.innerHTML=`<div class="panel-heading"><div><h2>已保存指令库</h2><p>BC：192.168.1.112 / .113；PSS：192.168.1.111（三位编号，如PSS001）；KC：192.168.1.137。YKE、YKM为BIN注入指令。每类独立编号，同一指令可跨类别拥有多个编号。接收先按数据区包含完整BC令进行匹配，命中后只采用BC及其绑定；未命中按功能ID、接口类型和完整数据区匹配。XML先排除包含BC数据区的项，其余文件和手动入库继续按完整UDP判重。</p></div><div class="import-action-line"><button id="command-catalog-clear" class="button" type="button">清空指令配置</button><a id="command-csv-export" class="button" href="/api/send-monitor/commands/export.csv" download="command-catalog.csv">导出指令库CSV</a></div></div><div class="section-pad"><div class="rule-toolbar"><input id="command-catalog-search" placeholder="搜索编号、名称或目的IP"><select id="command-catalog-kind" aria-label="指令类别"><option value="">全部类别</option><option>BC</option><option>B指令</option><option>广播令</option><option>PSS</option><option>KC</option><option>YKE</option><option>YKM</option><option>ZK</option><option>未分类</option></select><button id="command-catalog-refresh" class="button">查询 / 刷新</button></div><p id="command-catalog-summary" class="muted small-text"></p><p id="command-catalog-error" class="form-error" role="alert"></p><div class="table-scroll"><table class="data-table"><thead><tr><th>指令编号</th><th>指令名称</th><th>类别</th><th>目的IP</th><th>UDP字节数</th><th>源码</th><th>操作</th></tr></thead><tbody id="command-catalog-rows"></tbody></table></div><div class="current-table-pagination"><span id="command-catalog-page"></span><div><button id="command-catalog-previous" class="button small">上一页</button> <button id="command-catalog-next" class="button small">下一页</button></div></div><details id="command-import-details" class="import-details" open><summary>从Excel导入B指令 / 广播令，或导入指令CSV</summary><p class="muted small-text">Excel：选择包含“B指令”和/或“广播令”的.xlsx工作簿，按指令代号、指令名称、遥控包编码读取；B指令保留BC2000～BC3999，目的.113；广播令保留BC0001～BC0099，目的.112和.113。预留和超范围项跳过并提示，19字节B编码按已确认样例补齐为24字节并校验。外层使用已确认的1553模板，自动计算长度和累加和；仅建立识别配置，不发送报文。文件导入按完整UDP源码逐字节判重；封装字节不同可分别入库，不再只按数据区合并。</p><p class="muted small-text">CSV六列：指令编号、指令名称、指令类别、来源IP、目的IP、完整UDP载荷HEX；含XML指令时附加“指令格式”列，STC及STC-LEGACY4/2标记须保留。多个目的IP用分号分隔；编号留空可自动分配。合并导入，保留未出现的原指令，同源码可更新名称、追加目的IP和类别；已有分类编号不重新编号。有错误时整批不导入。停止接收时保存立即生效，运行中按2分钟周期统一切换。</p><div class="rule-toolbar"><label class="file-field">选择Excel / CSV<input id="command-csv-file" type="file" accept=".xlsx,.csv"></label><button id="command-csv-read" class="button">读取并预览</button><button id="command-csv-apply" class="button primary" disabled>应用批量导入</button></div><p id="command-csv-error" class="form-error" role="alert"></p><div id="command-csv-preview" class="hidden"><div id="command-csv-summary" class="import-summary"></div><div id="command-csv-errors" class="form-error"></div><div class="table-scroll"><table class="data-table"><thead><tr><th>编号</th><th>名称</th><th>类别</th><th>目的IP</th><th>UDP字节数</th></tr></thead><tbody id="command-csv-rows"></tbody></table></div></div></details></div>`;panel.after(catalogPanel);
  const clearKind=element('select');clearKind.id='command-clear-kind';clearKind.setAttribute('aria-label','清空指令类别');
  clearKind.replaceChildren(new Option('全部指令库',''),...['ZK','BC','PSS','KC','YKE','YKM'].map(k=>new Option('仅清空 '+k,k)));
  $('command-catalog-clear').before(clearKind);$('command-catalog-clear').textContent='清空所选指令库';
  const injectionPanel=element('details','import-details');
  injectionPanel.innerHTML=`<summary>从BIN文件导入注入指令</summary><p class="muted small-text">读取.bin二进制文件，按本次注入模板组包：EB 90 EB 90发送头、4字节接口类型20 00 00 00、路由11 04、文件全部字节、4字节小端累加和。自动填写外层长度，文件内的SPP内容和校验按原样保留。仅建立指令识别配置。</p><form id="command-bin-form"><div class="form-grid"><label class="file-field">选择BIN文件<input id="command-bin-file" type="file" accept=".bin" required><small>单条UDP最多容纳65475字节文件内容。</small></label><label>注入指令类别<select id="command-bin-kind"><option value="YKE">YKE</option><option value="YKM">YKM</option></select><small>两类分别从0001顺序编号，可手动修改编号。</small></label><label>指令名称<input id="command-bin-name" maxlength="256" placeholder="默认使用文件名"><small>可填写便于判读时识别的名称。</small></label><label>指令编号<input id="command-bin-code" maxlength="32" placeholder="优先取文件编号，无编号时顺序分配"><small>文件名含YKE或YKM编号时自动读取类别、编号和名称，例如ykm0021-test.bin；可修改，未识别编号时按所选类别顺序分配。</small></label><label>目的IP<input id="command-bin-destination" value="192.168.1.113" required><small>默认192.168.1.113，注入指令归入所选YKE或YKM类别。</small></label></div><div class="import-action-line"><button id="command-bin-read" class="button" type="submit">读取并预览注入指令</button><button id="command-bin-apply" class="button primary" type="button" disabled>确认入库</button><span class="muted small-text">确认入库后在下一次统一切换时生效。</span></div></form><p id="command-bin-error" class="form-error" role="alert"></p><div id="command-bin-preview" class="hidden"><div id="command-bin-summary" class="import-summary"></div><p id="command-bin-result" class="muted small-text"></p><div id="command-bin-errors" class="form-error"></div><label>合并后的完整UDP码字<textarea id="command-bin-hex" class="mono" rows="6" readonly spellcheck="false"></textarea></label></div>`;
  catalogPanel.querySelector('.section-pad').append(injectionPanel);
  const monitorConfig=element('section','panel');monitorConfig.id='command-monitor-config';
  monitorConfig.innerHTML='<div class="panel-heading"><div><h2>发送监视配置</h2><p>配置被动抓包范围；顶部“开始监听 / 停止接收”统一启停接收和发送监视。</p></div></div><div class="section-pad"></div>';
  monitorConfig.querySelector('.section-pad').append($('send-monitor-form'),$('send-monitor-driver'),$('send-monitor-dirty'));
  const monitorConfigError=element('p','form-error');monitorConfigError.id='send-monitor-config-error';monitorConfig.querySelector('.section-pad').append(monitorConfigError);
  const libraryInfo=element('div','import-action-line');libraryInfo.append($('send-commands-summary'),$('send-commands-export'));
  catalogPanel.querySelector('.section-pad').prepend(libraryInfo,$('send-commands-error'));
  configView.append(monitorConfig,catalogPanel);
  const dialog=element('dialog','packet-dialog');dialog.id='send-monitor-dialog';
  dialog.innerHTML=`<div class="dialog-heading"><h2 id="send-monitor-detail-title">发送报文完整内容</h2><button id="send-monitor-detail-close" class="icon-button" aria-label="关闭发送原文">×</button></div><div class="section-pad"><p id="send-monitor-detail-note" class="muted small-text"></p><label>完整UDP载荷<textarea id="send-monitor-detail-udp" rows="7" readonly spellcheck="false"></textarea></label><label>完整抓包帧（含网络头）<textarea id="send-monitor-detail-frame" rows="7" readonly spellcheck="false"></textarea></label></div>`;
  document.body.append(dialog);
  const editDialog=element('dialog','packet-dialog');editDialog.id='command-edit-dialog';
  editDialog.innerHTML=`<div class="dialog-heading"><h2>编辑已保存指令</h2><button type="button" id="command-edit-close" class="icon-button" aria-label="关闭指令编辑">×</button></div><form id="command-edit-form" class="section-pad"><div class="form-grid"><label>指令名称<input id="command-edit-name" required maxlength="256"></label><label>指令编号<input id="command-edit-code" required maxlength="32" spellcheck="false"></label><label id="command-edit-destinations-field" class="hidden">目的IP<input id="command-edit-destinations" placeholder="多个IP用逗号分隔"><small>STC指令可修改目的IP；修改ZK/BC/KC/PSS前缀后，请核对实际发送目标。</small></label></div><p id="command-edit-note" class="muted small-text"></p><p class="muted small-text">修改编号时，会同步更新已保存常态化规则中的TC引用和序列的指令项。手动TC绑定继续有效，历史记录保留当时名称和编号。停止接收时保存立即生效，运行中每2分钟统一切换。</p><p id="command-edit-error" class="form-error" role="alert"></p><div class="import-action-line"><button type="button" id="command-edit-cancel" class="button">取消</button><button type="submit" id="command-edit-save" class="button primary">保存修改</button></div></form>`;
  document.body.append(editDialog);
  let editingCommand=null;

  let draftMonitorSettings=null;
  let info={},adapters=[],available=null,busy=false,dirty=false,initialized=false,loading=false,listing=false,revision=-1,nextRefresh=0,generation=0,requestError='',driverError='';
  let catalogData=null,catalogPage=1,catalogRevision=-1,catalogLoading=false,catalogNextRefresh=0,catalogBusy=false,commandPreview=null,commandFileEpoch=0,sendCommandRevision=-1;
  let injectionPreview=null,injectionFileEpoch=0;
  const commandCancel=element('button','button','取消');commandCancel.id='command-csv-cancel';commandCancel.type='button';$('command-csv-read').after(commandCancel);
  const commandClear=addExcelImportClear('command-csv-clear','command-csv-file',commandCancel,()=>{
    if(catalogBusy&&!commandReading)return;discardCommandImport();toast('已清空本次指令文件读取');
  });
  let commandReading=false;
  function revokeCommandPreview(token){if(token)api('send-monitor/commands/preview/cancel',{token}).catch(()=>{});}
  function discardCommandImport(clearFile=true){
    revokeCommandPreview(commandPreview?.token);commandFileEpoch++;commandPreview=null;if(commandReading){commandReading=false;catalogBusy=false;}
    if(clearFile)$('command-csv-file').value='';$('command-csv-preview').classList.add('hidden');$('command-csv-error').textContent='';
    $('command-csv-summary').textContent='';$('command-csv-errors').replaceChildren();$('command-csv-rows').replaceChildren();render(app.state);
  }
  commandCancel.addEventListener('click',()=>{if(catalogBusy&&!commandReading)return;discardCommandImport();toast('已取消指令导入，指令库未修改');});
  function clearImportPreviews(){window.CommandXmlUI?.cancel();window.CommandManualUI?.cancel();revokeCommandPreview(commandPreview?.token);revokeCommandPreview(injectionPreview?.token);commandPreview=null;injectionPreview=null;$('command-csv-preview').classList.add('hidden');$('command-bin-preview').classList.add('hidden');}
  const fields=['send-monitor-source','send-monitor-destination','send-monitor-interface','send-monitor-port'];
  function fill(c){if(!c)return;$('send-monitor-source').value=(c.SourceIPs||[c.SourceIP]).join(', ');$('send-monitor-destination').value=(c.DestinationIPs||[c.DestinationIP]).join(', ');$('send-monitor-port').value=c.DestinationPort;$('send-monitor-interface').value=c.InterfaceId||'';}
  function render(s){
    if(!s)return;info=s.sendMonitor||info;if(app.view==='command-config'&&draftMonitorSettings)info={...info,configuration:draftMonitorSettings};const active=s.mode!=='stopped'||s.monitoringState==='stopping'||info.state==='running'||info.state==='stopping';
    if(info.configuration&&(!initialized||(!dirty&&!busy))){fill(info.configuration);initialized=true;}
    const labels={running:'监视中',stopping:'正在停止',stopped:'未启动 / 已停止',failed:'抓包异常'};
    setText('send-monitor-count',number(info.total));setText('send-monitor-status',labels[info.state]||'未启动');$('send-monitor-status').className='status-tag '+(info.state==='running'?'good':info.state==='failed'?'bad':'neutral');
    for(const id of fields)$(id).disabled=busy;
    $('send-monitor-save').disabled=busy;
    $('listen-button').disabled=app.busy;
    $('send-monitor-clear').disabled=active||busy;$('send-monitor-interfaces').disabled=busy||loading;
    setText('send-monitor-dirty',dirty?'设置已修改，请先保存；停止接收时立即生效，运行中每2分钟统一生效，保留原计数和记录。':'');
    setText('send-monitor-summary','本次启动 '+(info.startedAt||'—')+' · 网卡 '+(info.adapter||'—')+' · 已监测 '+number(info.total)+' 个UDP · 载荷 '+number(info.bytes)+' 字节 · 保留 '+number(info.retained)+'/200条 · 无法解析/分片 '+number(info.unsupported)+' · 截断 '+number(info.truncated)+' · 驱动丢包 '+(info.dropped==null?'暂不可用':number(info.dropped))+' · 网卡丢包 '+(info.interfaceDropped==null?'暂不可用':number(info.interfaceDropped)));
    setText('send-monitor-error',requestError||info.error||driverError);setText('send-monitor-config-error',requestError||info.error||driverError);
    const commands=info.commands||{};setText('send-commands-summary','指令配置 · 来源 '+(commands.sourceIP||'192.168.1.16')+' · 已保存 '+number(commands.count)+' 条不同指令 · 同源码合并目的IP · '+(commands.file||'config/observed-commands.json'));
    setText('send-commands-error',commands.error||'');$('send-commands-export').setAttribute('aria-disabled',commands.ready===true?'false':'true');
    if(app.view==='overview'&&(revision!==info.revision||sendCommandRevision!==commands.revision)&&Date.now()>=nextRefresh)refresh();
    $('command-catalog-clear').disabled=catalogBusy;
    for(const button of $('command-catalog-rows').querySelectorAll('[data-command-edit]'))button.disabled=catalogBusy;
    $('command-edit-save').disabled=catalogBusy;
    for(const id of ['command-edit-name','command-edit-destinations','command-edit-cancel','command-edit-close'])$(id).disabled=catalogBusy;
    $('command-edit-code').disabled=catalogBusy || !editingCommand?.code;
    commandCancel.disabled=(catalogBusy&&!commandReading)||!(commandReading||commandPreview||$('command-csv-file').files.length);
    commandClear.disabled=catalogBusy&&!commandReading;
    $('command-csv-read').disabled=catalogBusy;$('command-csv-apply').disabled=catalogBusy || !commandPreview?.canApply;
    $('command-bin-read').disabled=catalogBusy;$('command-bin-apply').disabled=catalogBusy || !injectionPreview?.canApply;
    if(app.view==='command-config' && info.commands?.ready && catalogRevision!==info.commands.revision && Date.now()>=catalogNextRefresh)loadCatalog();
  }
  async function load(){
    if(loading||!app.user)return;loading=true;const epoch=generation;
    try{
      const data=await api('send-monitor/interfaces');if(epoch!==generation)return;available=data.available;adapters=data.interfaces||[];driverError=data.error||'';
      const selected=$('send-monitor-interface').value||info.configuration?.InterfaceId||'';
      $('send-monitor-interface').replaceChildren(new Option('按来源列表自动选择',''),...adapters.map(a=>new Option(a.name+' · '+a.addresses.join(', ')+(a.loopback?'（回环）':''),a.id)));
      if(selected&&!adapters.some(a=>a.id===selected))$('send-monitor-interface').append(new Option('已保存网卡（当前不可用）',selected));
      $('send-monitor-interface').value=selected;
      setText('send-monitor-driver',available?'抓包驱动可用 · '+adapters.length+'个IPv4网卡 · 本机真实网卡抓包':'抓包驱动不可用；安装或启动Npcap后重启软件。');
    }catch(e){if(epoch===generation)driverError=e.message;}
    finally{if(epoch===generation){loading=false;render(app.state);}}
    if(app.view==='command-config'&&!dirty){try{const state=await api('send-monitor/state');draftMonitorSettings=state.configuration;fill(draftMonitorSettings);}catch(e){requestError=e.message;}}
    await refresh();
    await loadCatalog();
  }
  async function refresh(){
    if(listing||!app.user)return;listing=true;nextRefresh=Date.now()+350;const epoch=generation;
    try{
      const data=await api('send-monitor/list');if(epoch!==generation)return;revision=data.revision;sendCommandRevision=data.commandRevision;const rows=document.createDocumentFragment();
      for(const record of data.records){
        const row=element('tr');row.dataset.sendId=record.id;
        const command=element('td');command.dataset.field='command';
        if(record.commandMatched)command.append(element('div','mono',record.commandCode || '未分类'),element('div','small-text param-name',record.commandName));else command.append(element('span','status-tag neutral','未识别'));
        row.append(element('td','mono small-text','#'+record.id+'\n'+record.time),element('td','mono small-text',record.source+' → '+record.destination),command,element('td','mono',number(record.length)),element('td','mono small-text send-monitor-hex',record.preview));
        const cell=element('td'),button=element('button','text-button','查看 #'+record.id);button.addEventListener('click',()=>detail(record.id));cell.append(button);row.append(cell);rows.append(row);
      }
      if(!data.records.length){const row=element('tr'),cell=element('td','muted','暂无匹配的发送报文');cell.colSpan=6;row.append(cell);rows.append(row);}
      $('send-monitor-rows').replaceChildren(rows);
    }catch(e){if(epoch===generation){requestError=e.message;render(app.state);}}
    finally{if(epoch===generation)listing=false;}
  }
  async function detail(id){const epoch=generation;try{const data=await api('send-monitor/detail?id='+id);if(epoch!==generation)return;setText('send-monitor-detail-title','发送报文 #'+id);setText('send-monitor-detail-note',data.record.time+' · '+data.record.source+' → '+data.record.destination+' · '+data.record.length+'字节UDP载荷'+(data.record.commandMatched?' · 指令 '+(data.record.commandCode || '未分类')+' / '+data.record.commandName:' · 指令未识别'));$('send-monitor-detail-udp').value=data.udpHex;$('send-monitor-detail-frame').value=data.frameHex;dialog.showModal();}catch(e){toast(e.message,true);}}
  async function change(path,body){
    if(busy)return;busy=true;requestError='';render(app.state);const epoch=generation;
    try{const data=await api('send-monitor/'+path,body);if(epoch!==generation)return;info=data;if(path==='config')draftMonitorSettings=data.configuration;app.state.sendMonitor=data;dirty=false;fill(data.configuration);revision=-1;await refresh();}
    catch(e){if(epoch===generation)requestError=e.message;}
    finally{if(epoch===generation){busy=false;render(app.state);}}
  }
  for(const id of fields)$(id).addEventListener('input',()=>{dirty=true;requestError='';render(app.state);});
  $('send-monitor-form').addEventListener('submit',event=>{event.preventDefault();change('config',{SourceIPs:$('send-monitor-source').value.trim().split(/[，,;；\s]+/).filter(Boolean),DestinationIPs:$('send-monitor-destination').value.trim().split(/[，,;；\s]+/).filter(Boolean),InterfaceId:$('send-monitor-interface').value,DestinationPort:Number($('send-monitor-port').value)});});
  $('send-monitor-clear').addEventListener('click',()=>change('control',{action:'clear'}));
  $('send-commands-export').addEventListener('click',event=>{if(app.state?.sendMonitor?.commands?.ready!==true){event.preventDefault();toast(app.state?.sendMonitor?.commands?.error||'指令配置尚未就绪',true);}});
  $('send-monitor-interfaces').addEventListener('click',load);$('send-monitor-refresh').addEventListener('click',refresh);$('send-monitor-detail-close').addEventListener('click',()=>dialog.close());
  async function loadCatalog(){
    if(!app.user || catalogLoading)return;catalogLoading=true;catalogNextRefresh=Date.now()+1000;const epoch=generation,requestedPage=catalogPage,kind=$('command-catalog-kind').value,search=$('command-catalog-search').value.trim();
    try{const result=await api('send-monitor/commands/list?page='+requestedPage+'&kind='+encodeURIComponent(kind)+'&search='+encodeURIComponent(search));if(epoch!==generation || requestedPage!==catalogPage || kind!==$('command-catalog-kind').value || search!==$('command-catalog-search').value.trim())return;catalogData=result;catalogPage=result.page;catalogRevision=result.revision;const fragment=document.createDocumentFragment();for(const record of result.rows){const row=element('tr');row.dataset.commandCode=record.code;row.append(element('td','mono',record.code || '未分类 #'+record.number),element('td','param-name',record.name),element('td','',record.subtype ? record.kind+' / '+record.subtype : record.kind),element('td','mono small-text',record.destinations.join(', ')),element('td','mono',record.length));const cell=element('td','mono small-text',record.hexPreview || '（空载荷）');const detail=element('details','import-details');detail.append(element('summary','','完整UDP源码'));const content=element('pre','mono small-text');content.style.whiteSpace='pre-wrap';content.style.overflowWrap='anywhere';detail.append(content);detail.addEventListener('toggle',async()=>{if(!detail.open || detail.dataset.loaded)return;try{const data=await api('continuous-rules/tc/detail?number='+record.number);content.textContent=data.payloadHex || '（空载荷）';detail.dataset.loaded='yes';}catch(e){content.textContent=e.message;}});cell.append(detail);row.append(cell);const actions=element('td'),edit=element('button','button small','编辑');edit.type='button';edit.dataset.commandEdit=record.number;edit.disabled=catalogBusy;edit.addEventListener('click',()=>openCommandEditor(record,result.revision));actions.append(edit);row.append(actions);if(record.formatIssue)row.children[1].append(element('p','form-error small-text',record.formatIssue));fragment.append(row);}if(!result.rows.length){const row=element('tr'),cell=element('td','muted','暂无符合条件的已保存指令');cell.colSpan=7;row.append(cell);fragment.append(row);}$('command-catalog-rows').replaceChildren(fragment);$('command-catalog-summary').textContent='来源 '+result.sourceIP+' · '+result.sourceCount+'份不同源码 · '+result.total+'条分类记录 · 筛选'+result.matching+'条';$('command-catalog-page').textContent='第'+result.page+'/'+result.pages+'页';$('command-catalog-previous').disabled=catalogPage<=1;$('command-catalog-next').disabled=catalogPage>=result.pages;$('command-catalog-error').textContent='';}catch(e){if(epoch===generation)$('command-catalog-error').textContent=e.message;}finally{catalogLoading=false;}
  }
  function openCommandEditor(record,revision){
    if(catalogBusy)return;editingCommand={...record,revision};$('command-edit-name').value=record.name;$('command-edit-code').value=record.code || '';$('command-edit-code').required=!!record.code;$('command-edit-destinations-field').classList.toggle('hidden',!record.stc);$('command-edit-destinations').required=!!record.stc;$('command-edit-destinations').value=record.stc?record.destinations.join(', '):'';
    $('command-edit-note').textContent=record.stc?'XML STC指令：编号可使用ZK、BC、KC或PSS前缀，目的IP可单独修改；PSS至少3位数字，其余至少4位。':record.code?'类别 '+record.kind+' · 目的IP '+record.destinations.join(', ')+'。编号须保留'+record.kind+'前缀，至少'+(record.kind==='PSS'?'3':'4')+'位数字且大于0'+(record.kind==='PSS'?'，例如PSS001、PSS092':'')+'。同一源码各类别共用名称，本次只修改所选类别的编号。':'未分类指令可修改名称；编号须先按目的IP归类。';
    $('command-edit-error').textContent='';render(app.state);editDialog.showModal();$('command-edit-name').focus();
  }
  function closeCommandEditor(){if(!catalogBusy){editDialog.close();editingCommand=null;}}
  $('command-edit-cancel').addEventListener('click',closeCommandEditor);$('command-edit-close').addEventListener('click',closeCommandEditor);
  editDialog.addEventListener('cancel',event=>{if(catalogBusy)event.preventDefault();});editDialog.addEventListener('close',()=>{editingCommand=null;});
  $('command-edit-form').addEventListener('submit',async event=>{
    event.preventDefault();if(catalogBusy || !editingCommand)return;const record=editingCommand,epoch=generation;catalogBusy=true;render(app.state);$('command-edit-error').textContent='';
    try{
      const result=await api('send-monitor/commands/edit',{number:String(record.number),originalCode:record.code,code:$('command-edit-code').value.trim(),name:$('command-edit-name').value.trim(),revision:String(record.revision),destinationIPs:record.stc?$('command-edit-destinations').value.trim():''});
      if(epoch!==generation)return;editDialog.close();editingCommand=null;clearImportPreviews();commandFileEpoch++;injectionFileEpoch++;catalogRevision=-1;await loadCatalog();
      app.state=await api('state');render(app.state);toast('指令已修改'+(result.renamedRules || result.renamedSequenceRows?'，同步更新'+result.renamedRules+'条规则、'+result.renamedSequenceRows+'个序列指令项':''));
    }catch(error){if(epoch===generation)$('command-edit-error').textContent=error.message;}
    finally{if(epoch===generation){catalogBusy=false;render(app.state);}}
  });
  const catalogQuery=()=>{catalogPage=1;catalogRevision=-1;loadCatalog();};$('command-catalog-refresh').addEventListener('click',catalogQuery);$('command-catalog-kind').addEventListener('change',catalogQuery);$('command-catalog-previous').addEventListener('click',()=>{catalogPage--;catalogRevision=-1;loadCatalog();});$('command-catalog-next').addEventListener('click',()=>{catalogPage++;catalogRevision=-1;loadCatalog();});
  $('command-catalog-clear').addEventListener('click',async()=>{
    if(catalogBusy)return;catalogBusy=true;const epoch=generation;render(app.state);$('command-catalog-error').textContent='';
    try{
      const kind=$('command-clear-kind').value;const preview=await api('send-monitor/commands/clear-preview?'+new URLSearchParams({kind}));if(epoch!==generation)return;
      if(!window.confirm('确定清空'+(kind?kind+'类':'全部')+'指令吗？\n涉及'+preview.count+'份源码，清除'+preview.bindings+'个相关手动TC绑定。其他类别保留；判读规则和序列不自动删除，引用已清指令的步骤需重新配置。\n运行记录保留。'))return;
      await api('send-monitor/commands/clear',{kind,confirm:'clear-command-catalog',revision:String(preview.revision),tcRevision:preview.tcRevision});if(epoch!==generation)return;
      clearImportPreviews();commandFileEpoch++;injectionFileEpoch++;$('command-csv-error').textContent='';$('command-bin-error').textContent='';catalogPage=1;catalogRevision=-1;$('command-catalog-search').value='';$('command-catalog-kind').value='';
      await loadCatalog();await api('state').then(state=>{app.state=state;render(state);});toast('所选指令库及相关TC绑定已清空');
    }catch(e){if(epoch===generation)$('command-catalog-error').textContent=e.message;}
    finally{catalogBusy=false;render(app.state);}
  });
  $('command-csv-file').addEventListener('change',()=>discardCommandImport(false));
  $('command-csv-read').addEventListener('click',async()=>{const file=$('command-csv-file').files[0];if(!file){$('command-csv-error').textContent='请选择指令Excel或CSV文件';return;}if(catalogBusy)return;catalogBusy=true;commandReading=true;clearImportPreviews();const epoch=generation,fileEpoch=++commandFileEpoch;render(app.state);$('command-csv-error').textContent='';try{const response=await fetch('/api/send-monitor/commands/preview'+(/\.xlsx$/i.test(file.name)?'?format=bc-xlsx':''),{method:'POST',credentials:'same-origin',headers:{'Content-Type':/\.xlsx$/i.test(file.name)?'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet':'text/csv','X-CSRF-Token':app.user.csrf},body:file});const data=await response.json();if(!response.ok)throw new Error(data.error || '读取指令文件失败');if(epoch!==generation || fileEpoch!==commandFileEpoch){revokeCommandPreview(data.token);return;}commandPreview=data;$('command-csv-preview').classList.remove('hidden');$('command-csv-summary').textContent='读取'+data.total+'行 · 新增源码'+data.addedCommands+'份 · 新增分类'+data.addedCategories+'项 · 新增目的IP'+data.addedDestinations+'个 · 更新名称'+data.renamed+'项'+(data.bCommands || data.broadcastCommands?' · B指令'+data.bCommands+'条 · 广播令'+data.broadcastCommands+'条 · 跳过'+data.skipped+'条':'')+' · 错误'+data.errors.length+'项';$('command-csv-errors').replaceChildren(...(data.warnings||[]).slice(0,100).map(warning=>element('p','muted',warning)),...data.errors.slice(0,100).map(error=>element('p','',error)));const fragment=document.createDocumentFragment();for(const item of data.rows){const row=element('tr');for(const value of [item.code || '未分类',item.name,item.kind,item.destinations,item.payloadLength])row.append(element('td','small-text',Array.isArray(value)?value.join(', '):value));fragment.append(row);}$('command-csv-rows').replaceChildren(fragment);}catch(e){if(epoch===generation&&fileEpoch===commandFileEpoch)$('command-csv-error').textContent=e.message;}finally{if(epoch===generation&&fileEpoch===commandFileEpoch){commandReading=false;catalogBusy=false;render(app.state);}}});
  $('command-csv-apply').addEventListener('click',async()=>{if(catalogBusy || !commandPreview?.canApply)return;catalogBusy=true;render(app.state);const epoch=generation;try{await api('send-monitor/commands/apply',{token:commandPreview.token});if(epoch!==generation)return;commandPreview=null;$('command-csv-preview').classList.add('hidden');catalogRevision=-1;catalogPage=1;await loadCatalog();toast('指令库已合并导入');}catch(e){if(epoch===generation)$('command-csv-error').textContent=e.message;}finally{catalogBusy=false;render(app.state);}});
  const injectionFields=['command-bin-file','command-bin-kind','command-bin-name','command-bin-code','command-bin-destination'];
  function invalidateInjection(){injectionFileEpoch++;injectionPreview=null;$('command-bin-preview').classList.add('hidden');$('command-bin-error').textContent='';render(app.state);}
  for(const id of injectionFields)$(id).addEventListener(['command-bin-file','command-bin-kind'].includes(id)?'change':'input',invalidateInjection);
  $('command-bin-file').addEventListener('change',()=>{
    const file=$('command-bin-file').files[0];if(!file)return;
    const stem=file.name.replace(/\.bin$/i,''),identifiers=[...stem.matchAll(/(^|[^A-Za-z0-9])((?:YKE|YKM)[0-9]+)(?![A-Za-z0-9])/gi)];
    const identifier=identifiers.length===1?identifiers[0]:null;
    $('command-bin-code').value=identifier?identifier[2].toUpperCase():'';
    if(identifier)$('command-bin-kind').value=identifier[2].slice(0,3).toUpperCase();
    $('command-bin-name').value=identifier?(stem.slice(identifier.index+identifier[0].length).replace(/^[\s_\-－—–]+/u,'').trim()||stem):stem;
    if(identifiers.length>1)$('command-bin-error').textContent='文件名含有多个YKE/YKM编号，请手动指定指令编号后重新预览';
  });
  $('command-bin-kind').addEventListener('change',()=>{const code=$('command-bin-code');if(/^(YKE|YKM)[0-9]+$/i.test(code.value.trim()))code.value=$('command-bin-kind').value+code.value.trim().slice(3);});
  $('command-bin-code').addEventListener('input',()=>{const prefix=$('command-bin-code').value.trim().slice(0,3).toUpperCase();if(['YKE','YKM'].includes(prefix))$('command-bin-kind').value=prefix;});
  $('command-bin-form').addEventListener('submit',async event=>{
    event.preventDefault();const file=$('command-bin-file').files[0];if(!file){$('command-bin-error').textContent='请选择BIN文件';return;}if(catalogBusy)return;
    catalogBusy=true;clearImportPreviews();const epoch=generation,fileEpoch=injectionFileEpoch;render(app.state);$('command-bin-error').textContent='';
    try{
      const query=new URLSearchParams({format:'injection-bin',kind:$('command-bin-kind').value,filename:file.name,name:$('command-bin-name').value,code:$('command-bin-code').value.trim(),destinationIP:$('command-bin-destination').value.trim()});
      const response=await fetch('/api/send-monitor/commands/preview?'+query,{method:'POST',credentials:'same-origin',headers:{'Content-Type':'application/octet-stream','X-CSRF-Token':app.user.csrf},body:file});
      const data=await response.json();if(!response.ok)throw new Error(data.error || '读取注入文件失败');if(epoch!==generation || fileEpoch!==injectionFileEpoch)return;
      injectionPreview=data;$('command-bin-preview').classList.remove('hidden');
      $('command-bin-summary').textContent='BIN '+data.injectionBytes+'字节 · 完整UDP '+(data.injectionBytes+32)+'字节 · 新增源码'+data.addedCommands+'份 · 新增分类'+data.addedCategories+'项 · 新增目的IP'+data.addedDestinations+'个 · 更新名称'+data.renamed+'项 · 错误'+data.errors.length+'项';
      const item=data.rows[0];$('command-bin-result').textContent=(item?'指令 '+(item.code || '未分配')+' / '+item.name+' · 目的 '+item.destinations+' · ':'')+'外层长度值 '+(data.injectionBytes+24)+' · 小端累加和 '+data.checksumHex;
      $('command-bin-hex').value=data.packetHex;$('command-bin-errors').replaceChildren(...data.errors.map(error=>element('p','',error)));
    }catch(e){if(epoch===generation && fileEpoch===injectionFileEpoch)$('command-bin-error').textContent=e.message;}
    finally{catalogBusy=false;render(app.state);}
  });
  $('command-bin-apply').addEventListener('click',async()=>{
    if(catalogBusy || !injectionPreview?.canApply)return;catalogBusy=true;render(app.state);const epoch=generation;
    try{await api('send-monitor/commands/apply',{token:injectionPreview.token});if(epoch!==generation)return;injectionPreview=null;$('command-bin-preview').classList.add('hidden');catalogRevision=-1;catalogPage=1;await loadCatalog();toast('注入指令已保存到指令库');}
    catch(e){if(epoch===generation)$('command-bin-error').textContent=e.message;}
    finally{catalogBusy=false;render(app.state);}
  });
  function reset(){window.CommandXmlUI?.reset();window.CommandManualUI?.reset();generation++;draftMonitorSettings=null;commandReading=false;catalogBusy=false;editingCommand=null;editDialog.close();info={};adapters=[];available=null;busy=dirty=initialized=loading=listing=false;revision=-1;nextRefresh=0;requestError=driverError='';catalogData=null;catalogPage=1;catalogRevision=-1;sendCommandRevision=-1;clearImportPreviews();commandFileEpoch++;injectionFileEpoch++;$('command-bin-form').reset();$('command-bin-error').textContent='';$('command-bin-hex').value='';$('command-catalog-rows').replaceChildren();$('command-csv-error').textContent='';$('send-monitor-rows').replaceChildren();$('send-monitor-detail-udp').value='';$('send-monitor-detail-frame').value='';dialog.close();}
  return {render,load,reset,refresh,loadCatalog,discardImports:clearImportPreviews};
})();
