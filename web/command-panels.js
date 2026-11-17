'use strict';
window.CommandPanelUI=(()=>{
  names['command-panels']=['指令面板','切换画面，点击按钮发送所关联的库内指令。'];
  names['command-panel-config']=['面板配置','自由排布指令按钮，设置文字、大小并关联库内指令。'];
  const clone=value=>JSON.parse(JSON.stringify(value)),id=prefix=>prefix+crypto.getRandomValues(new Uint32Array(2)).join(''),key=(screen,button)=>screen+'/'+button;
  let saved=null,draft=null,bindings=new Map(),draftBindings=new Map(),screenId='',editId='',buttonId='',dirty=false,loading=false,saving=false,sending=false,drag=null,epoch=0,nextPoll=0,lastLibrary=-1,cooldown=0;
  let pickerPage=1,pickerPages=1,pickerBusy=false,pickerRequest=0,pickerTarget=null,choosing=false;
  const results=new Map(),positions=new Map(),senders=new Map();let renderedScreenId='';
  const view=element('section','view hidden');view.id='view-command-panels';
  view.innerHTML=`<section class="cp-monitor"><nav id="cp-screen-tabs" class="cp-screen-tabs" aria-label="指令画面"></nav><div class="cp-viewport" id="cp-monitor-viewport"><div id="cp-monitor-canvas" class="cp-canvas" aria-label="指令按钮面板"></div></div></section><div class="cp-status"><span id="cp-last-result" role="status" aria-live="polite">点击按钮，向该指令已配置的全部目的IP各发送一次。</span><button id="cp-open-config" type="button" class="text-button">配置画面</button></div><p id="cp-monitor-error" class="form-error" role="alert"></p>`;
  const config=element('section','view hidden');config.id='view-command-panel-config';
  config.innerHTML=`<section class="panel cp-editor-panel"><div class="panel-heading"><div><h2>指令画面与按钮布局</h2><p>按账号保存；在配置画布中拖动、缩放按钮并关联指令。配置操作不发送指令。</p></div><span id="cp-save-state" class="muted small-text">尚未载入</span></div><div class="cp-toolbar"><select id="cp-edit-screen" aria-label="选择配置的指令画面"></select><input id="cp-screen-name" aria-label="指令画面名称" maxlength="40"><button type="button" id="cp-add-screen" class="button small">＋画面</button><button type="button" id="cp-delete-screen" class="button small">删除画面</button><button type="button" id="cp-add-button" class="button small">＋指令按钮</button><button type="button" id="cp-properties" class="button small" aria-controls="cp-inspector" aria-expanded="false">按钮设置</button><button type="button" id="cp-reload" class="button small">取消修改 / 重新载入</button><button type="button" id="cp-save" class="button small primary">保存全部画面</button><button type="button" id="cp-open-panel" class="button small">查看面板</button></div><div class="cp-hint"><span>拖动按钮上沿移动，拖动右下角缩放；选中移动柄后方向键微调，Shift为1像素。按钮至少80×44像素。</span><output id="cp-geometry"></output></div><p id="cp-config-error" class="form-error cp-message" role="alert"></p><div class="cp-editor-stage"><div id="cp-editor-viewport" class="cp-viewport"><div id="cp-editor-canvas" class="cp-canvas cp-edit" aria-label="指令按钮配置画布"></div></div><section id="cp-inspector" class="cp-inspector hidden" aria-label="指令按钮设置"><div class="cp-inspector-heading"><h3>按钮设置</h3><button type="button" id="cp-close-properties" class="icon-button" aria-label="收起指令按钮设置">×</button></div><div class="cp-fields"><label class="cp-wide">按钮文字<input id="cp-button-label" maxlength="80" placeholder="留空时显示指令名称"></label><label>X / px<input id="cp-x" type="number" min="0" max="10000" step="1"></label><label>Y / px<input id="cp-y" type="number" min="0" max="10000" step="1"></label><label>宽度 / px<input id="cp-width" type="number" min="80" max="10000" step="1"></label><label>高度 / px<input id="cp-height" type="number" min="44" max="10000" step="1"></label><label>字号 / px<input id="cp-font" type="number" min="12" max="40" step="1"></label><label>按钮颜色<select id="cp-theme"><option value="teal">青绿</option><option value="blue">蓝色</option><option value="amber">金色</option><option value="slate">深灰</option></select></label></div><div class="cp-binding"><span class="muted small-text">关联指令</span><strong id="cp-binding-name"></strong><span id="cp-binding-destinations" class="mono small-text"></span><div class="cp-binding-actions"><button id="cp-select-command" type="button" class="button small primary">选择库内指令</button><button id="cp-unbind" type="button" class="button small">解除关联</button></div></div><button id="cp-delete-button" type="button" class="text-button cp-delete">删除此按钮</button></section></div></section>`;
  document.querySelector('.page-footer').before(view,config);
  const copyButton=element('button','button small','复制控件'),pasteButton=element('button','button small','粘贴控件');
  copyButton.id='cp-copy-button';pasteButton.id='cp-paste-button';copyButton.type=pasteButton.type='button';
  copyButton.title='复制选中指令按钮（Ctrl+C）';pasteButton.title='粘贴到当前画面（Ctrl+V），支持跨画面';
  $('cp-add-button').after(copyButton,pasteButton);
  const addSender=element('button','button small','＋指令发送框');addSender.id='cp-add-sender';addSender.type='button';$('cp-add-button').after(addSender);
  const picker=element('dialog','cp-picker');picker.id='cp-command-picker';picker.setAttribute('aria-labelledby','cp-picker-title');
  picker.innerHTML=`<div class="dialog-heading"><div><h2 id="cp-picker-title">选择按钮关联的指令</h2><p class="muted small-text">可选择任意类别；广播指令按库内该编号的全部目的IP发送。</p></div><button type="button" id="cp-picker-close" class="icon-button" aria-label="取消选择指令">×</button></div><div class="cp-picker-toolbar"><input id="cp-picker-search" aria-label="搜索关联指令" placeholder="搜索指令编号、名称或目的IP"><select id="cp-picker-kind" aria-label="关联指令类别"><option value="">全部类别</option><option>BC</option><option>B指令</option><option>广播令</option><option>PSS</option><option>KC</option><option>YKE</option><option>YKM</option><option>ZK</option><option>未分类</option></select><button id="cp-picker-query" type="button" class="button small">查询</button></div><p id="cp-picker-error" class="form-error" role="alert"></p><div class="cp-picker-table"><table class="data-table"><thead><tr><th>指令编号</th><th>指令名称</th><th>目的IP</th><th>操作</th></tr></thead><tbody id="cp-picker-rows"></tbody></table></div><div class="cp-picker-footer"><span id="cp-picker-page" class="muted small-text"></span><div><button id="cp-picker-prev" type="button" class="button small">上一页</button> <button id="cp-picker-next" type="button" class="button small">下一页</button> <button id="cp-picker-cancel" type="button" class="button small">取消</button></div></div>`;
  document.body.append(picker);
  const activeScreen=()=>saved?.Screens.find(s=>s.Id===screenId)||saved?.Screens[0],editScreen=()=>draft?.Screens.find(s=>s.Id===editId)||draft?.Screens[0],currentButton=()=>editScreen()?.Buttons.find(b=>b.Id===buttonId);
  const arranger=window.LayoutArrange.attach({prefix:'cp',view:'command-panel-config',before:config.querySelector('.cp-hint'),canvas:$('cp-editor-canvas'),
    items:()=>editScreen()?.Buttons,context:()=>app.user?.username+':'+editId,primary:()=>buttonId,setPrimary:value=>{buttonId=value;},blocked:()=>saving||loading||!!drag,
    onSelection:refreshSelection,onChange:refreshGeometry,error:message=>{$('cp-config-error').textContent=message;toast(message,true);}});
  function refreshSelection(){
    arranger.sync();for(const el of $('cp-editor-canvas').children){el.classList.toggle('cp-selected',arranger.isSelected(el.dataset.buttonId));el.classList.toggle('layout-primary',el.dataset.buttonId===buttonId);el.querySelector('.cp-drag')?.setAttribute('aria-pressed',String(arranger.isSelected(el.dataset.buttonId)));}renderInspector();
  }
  function refreshGeometry(items){
    if(!items.length)return;
    const nodes=new Map([...$('cp-editor-canvas').children].map(node=>[node.dataset.buttonId,node]));for(const item of items){const node=nodes.get(item.Id);if(node)geometry(node,item);}
    fit($('cp-editor-canvas'),editScreen());updateGeometry();mark();
  }
  const storageKey=()=> 'telemetry.command-screen.v1:'+encodeURIComponent(app.user?.username||'');
  function infoFor(screen,button,editable){return(editable?draftBindings:bindings).get(key(screen.Id,button.Id))||{available:false,error:'未关联指令',code:button.CommandCode,name:'',destinations:[]};}
  function mark(){dirty=true;setText('cp-save-state','有未保存修改');setControls();}
  function setControls(){
    const blocked=saving||(loading&&!draft);
    for(const node of config.querySelectorAll('input,select,button'))node.disabled=blocked;
    $('cp-delete-screen').disabled=blocked||!draft||draft.Screens.length<=1;
    $('cp-properties').disabled=blocked||!currentButton();$('cp-save').disabled=saving||loading||!dirty;
    copyButton.disabled=blocked||!currentButton();pasteButton.disabled=blocked||!editScreen()||!window.LayoutClipboard.has('command-button');
    for(const button of $('cp-monitor-canvas').querySelectorAll('.cp-command'))button.disabled=sending||Date.now()<cooldown||button.dataset.available!=='true';
    for(const button of $('cp-monitor-canvas').querySelectorAll('.cp-sender-send'))button.disabled=sending||Date.now()<cooldown||!!senders.get(button.closest('.cp-sender-shell').dataset.senderKey)?.listing;
    $('cp-picker-prev').disabled=pickerBusy||choosing||pickerPage<=1;$('cp-picker-next').disabled=pickerBusy||choosing||pickerPage>=pickerPages;
    for(const node of picker.querySelectorAll('input,select,#cp-picker-query,#cp-picker-rows button'))node.disabled=pickerBusy||choosing;
    arranger.sync();
  }
  function geometry(el,button){Object.assign(el.style,{left:button.X+'px',top:button.Y+'px',width:button.Width+'px',height:button.Height+'px'});el.style.setProperty('--cp-font',button.FontSize+'px');}
  function fit(canvas,screen){canvas.style.width=Math.max(0,...(screen?.Buttons||[]).map(b=>b.X+b.Width+28))+'px';canvas.style.height=Math.max(0,...(screen?.Buttons||[]).map(b=>b.Y+b.Height+28))+'px';}
  function viewportSize(){if(!app.user)return;for(const id of ['cp-monitor-viewport','cp-editor-viewport']){const el=$(id);if(el.getClientRects().length)el.style.height=Math.max(300,window.innerHeight-el.getBoundingClientRect().top-68)+'px';}}
  function remember(){const v=$('cp-monitor-viewport');if(renderedScreenId&&v.getClientRects().length)positions.set(renderedScreenId,{left:v.scrollLeft,top:v.scrollTop});}
  function buttonFace(button,info){const face=element('span','cp-button-face');face.append(element('span','cp-label',button.Label||info.name||'未关联指令'),element('span','cp-code',info.code||(info.available?'未分类 #'+info.number:'请选择指令')));return face;}
  function buildSender(shell,screen,box){
    const value=key(screen.Id,box.Id);let state=senders.get(value);
    if(!state){state={key:value,search:'',kind:'',page:1,pages:1,rows:null,library:-1,selected:null,preview:null,error:'',request:0,previewRequest:0,listing:false,previewOpen:false};senders.set(value,state);}
    state.root=shell;shell.dataset.senderKey=value;
    shell.innerHTML=`<strong class="cp-sender-title"></strong><div class="cp-sender-tools"><input class="cp-sender-search" aria-label="搜索框内指令" placeholder="编号、名称、目的IP"><select class="cp-sender-kind" aria-label="框内指令类别"><option value="">全部类别</option><option>BC</option><option>B指令</option><option>广播令</option><option>PSS</option><option>KC</option><option>YKE</option><option>YKM</option><option>ZK</option><option>未分类</option></select><button type="button" class="button small cp-sender-query">查询</button></div><p class="cp-sender-error form-error" role="alert"></p><div class="cp-sender-table"><table class="data-table"><thead><tr><th>编号</th><th>名称</th><th>目的IP</th><th>操作</th></tr></thead><tbody></tbody></table></div><div class="cp-sender-pager"><span></span><div><button type="button" class="button small cp-sender-prev">上一页</button> <button type="button" class="button small cp-sender-next">下一页</button></div></div><details class="cp-sender-preview"><summary>选中指令行查看发送内容</summary><div></div></details><p class="cp-sender-feedback"></p>`;
    shell.querySelector('.cp-sender-title').textContent=box.Label||'指令发送';const input=shell.querySelector('input'),kind=shell.querySelector('select');input.value=state.search;kind.value=state.kind;
    input.addEventListener('input',()=>{state.search=input.value;});
    const query=()=>{state.page=1;state.selected=state.preview=null;state.previewRequest++;loadSender(state);};input.addEventListener('keydown',event=>{if(event.key==='Enter'){event.preventDefault();query();}});kind.addEventListener('change',()=>{state.kind=kind.value;query();});shell.querySelector('.cp-sender-query').addEventListener('click',query);
    shell.querySelector('.cp-sender-prev').addEventListener('click',()=>{state.page--;loadSender(state);});shell.querySelector('.cp-sender-next').addEventListener('click',()=>{state.page++;loadSender(state);});shell.querySelector('details').addEventListener('toggle',event=>{state.previewOpen=event.currentTarget.open;});
    updateSender(state);if(state.rows===null||state.library!==lastLibrary)queueMicrotask(()=>{if(!state.listing)loadSender(state);});
  }
  function updateSender(state){
    const shell=state.root;if(!shell)return;const body=shell.querySelector('tbody');body.replaceChildren();
    for(const record of state.rows||[]){
      const row=element('tr',state.selected?.number===record.number&&state.selected?.code===record.code?'cp-sender-selected':'');row.dataset.number=record.number;row.dataset.code=record.code;row.tabIndex=0;row.title='点击预览 '+(record.code||'未分类')+' '+record.name;
      row.append(element('td','mono',record.code||'#'+record.number),element('td','',record.name),element('td','mono small-text',record.destinations.join(', ')));
      const cell=element('td'),button=element('button','button small primary cp-sender-send','发送');button.type='button';button.disabled=state.listing||sending||Date.now()<cooldown;button.setAttribute('aria-label','发送指令 '+(record.code||'#'+record.number));button.addEventListener('click',event=>{event.stopPropagation();senderSend(state,record);});cell.append(button);row.append(cell);
      row.addEventListener('click',()=>previewSender(state,record));row.addEventListener('keydown',event=>{if(event.target===row&&['Enter',' '].includes(event.key)){event.preventDefault();previewSender(state,record);}});body.append(row);
    }
    if(!body.children.length){const row=element('tr'),cell=element('td','muted',state.listing?'正在读取指令库…':'没有符合条件的指令');cell.colSpan=4;row.append(cell);body.append(row);}
    shell.querySelector('.cp-sender-error').textContent=state.error;shell.querySelector('.cp-sender-pager>span').textContent='第'+state.page+'/'+state.pages+'页'+(state.matching==null?'':' · '+state.matching+'条');
    shell.querySelector('.cp-sender-query').disabled=state.listing;shell.querySelector('.cp-sender-prev').disabled=state.listing||state.page<=1;shell.querySelector('.cp-sender-next').disabled=state.listing||state.page>=state.pages;
    const details=shell.querySelector('details'),summary=details.querySelector('summary'),area=details.querySelector('div');area.replaceChildren();
    summary.textContent=state.selected?'发送内容预览 · '+(state.selected.code||'未分类')+' '+state.selected.name:'选中指令行查看发送内容';
    if(state.preview){const plan=state.preview;area.append(element('p','mono cp-sender-routes',plan.routes.map(route=>plan.sourceIP+':'+route.SourcePort+' → '+route.DestinationIP+':'+route.DestinationPort).join('\n')));const columns=element('div','cp-sender-code-columns');for(const [label,hex] of [['完整UDP源码',plan.hex],['数据内容区',plan.dataContentRecognized?plan.dataContentHex:'未识别到独立的数据内容区']]){const column=element('div');column.append(element('span','',label),element('pre','mono',hex));columns.append(column);}area.append(columns);}
    details.open=state.previewOpen;const result=results.get(state.key);shell.querySelector('.cp-sender-feedback').textContent=result?.text||'点击行预览，点击“发送”发出；使用现有IP和端口配置。';
  }
  async function loadSender(state){
    const turn=epoch,request=++state.request;state.listing=true;state.error='';updateSender(state);
    try{const data=await api('send-monitor/commands/list?page='+state.page+'&search='+encodeURIComponent(state.search.trim())+'&kind='+encodeURIComponent(state.kind));if(turn!==epoch||request!==state.request)return;
      if(state.library!==data.revision){state.selected=state.preview=null;state.previewRequest++;}state.rows=data.rows;state.page=data.page;state.pages=data.pages;state.matching=data.matching;state.library=data.revision;
    }catch(error){if(turn===epoch&&request===state.request)state.error=error.message;}
    finally{if(turn===epoch&&request===state.request){state.listing=false;updateSender(state);}}
  }
  async function previewSender(state,record){
    if(state.listing)return;
    const turn=epoch,request=++state.previewRequest;state.selected=record;state.preview=null;state.error='';state.previewOpen=true;updateSender(state);
    try{const plan=await api('command-send/preview',{number:record.number,code:record.code});if(turn!==epoch||request!==state.previewRequest)return;state.preview=plan;state.error=plan.error||'';}
    catch(error){if(turn===epoch&&request===state.previewRequest)state.error=error.message;}
    finally{if(turn===epoch&&request===state.previewRequest)updateSender(state);}
  }
  function senderSend(state,record){
    if(state.listing||sending||Date.now()<cooldown)return;state.selected=record;state.previewRequest++;
    return transmit(async()=>{const plan=await api('command-send/preview',{number:record.number,code:record.code});if(state.selected===record){state.preview=plan;updateSender(state);}return plan;},record,state.key);
  }
  function draw(canvas,screen,editable){
    const v=canvas.parentElement,scroll={left:v.scrollLeft,top:v.scrollTop};canvas.replaceChildren();if(!screen)return;
    if(editable)arranger.sync();
    for(const b of screen.Buttons){
      const info=infoFor(screen,b,editable),wrap=element('div','cp-button-wrap cp-'+b.Theme+(editable?' cp-edit-button':'')+(editable&&arranger.isSelected(b.Id)?' cp-selected':''));wrap.dataset.buttonId=b.Id;geometry(wrap,b);
      const isSender=b.Type==='sender';if(isSender)wrap.classList.add('cp-sender-wrap');
      const button=element(isSender?'div':'button',isSender?'cp-sender-shell':'cp-command');if(!isSender){button.type='button';button.dataset.available=String(info.available);button.append(buttonFace(b,info));button.title=[b.Label,info.code,info.name,(info.destinations||[]).join(', '),info.error].filter(Boolean).join('\n');}
      if(editable){
        button.addEventListener('focus',()=>selectButton(b.Id,false,null,true));
        button.addEventListener('pointerdown',event=>{if(event.button===0){event.preventDefault();selectButton(b.Id,false,event,true);}});
        button.setAttribute('aria-label','设置'+(isSender?'发送框 ':'按钮 ')+(b.Label||info.name||b.Id));button.addEventListener('click',event=>{if(!event.ctrlKey&&!event.metaKey&&!event.shiftKey)selectButton(b.Id,true,null,true);});
        if(isSender){button.tabIndex=0;button.append(element('strong','cp-sender-title',b.Label||'指令发送'),element('p','cp-sender-placeholder','运行时在此搜索、按类别筛选库内指令，选中后预览并发送。'));button.addEventListener('keydown',e=>{if(e.key==='Enter'){e.preventDefault();selectButton(b.Id,true);}});}
        const move=element('button','cp-drag','⠿'),resize=element('button','cp-resize','↘');move.type=resize.type='button';move.setAttribute('aria-label','移动指令按钮');resize.setAttribute('aria-label','调整指令按钮大小');wrap.append(move,resize);
        for(const [control,type] of [[move,'move'],[resize,'resize']]){
          control.addEventListener('focus',()=>selectButton(b.Id,false,null,true));
          control.addEventListener('pointerdown',e=>startDrag(e,b,wrap,type));control.addEventListener('pointermove',moveDrag);control.addEventListener('pointerup',endDrag);control.addEventListener('pointercancel',endDrag);
          control.addEventListener('keydown',e=>{if(!['ArrowLeft','ArrowRight','ArrowUp','ArrowDown'].includes(e.key)||saving||loading)return;e.preventDefault();selectButton(b.Id,false,null,type==='move');const step=e.shiftKey?1:10,dx=e.key==='ArrowLeft'?-step:e.key==='ArrowRight'?step:0,dy=e.key==='ArrowUp'?-step:e.key==='ArrowDown'?step:0;if(type==='move')arranger.nudge(dx,dy);else{b.Width=Math.max(isSender?440:80,Math.min(10000,b.Width+dx));b.Height=Math.max(isSender?300:44,Math.min(10000,b.Height+dy));refreshGeometry([b]);}});
        }
      }else{
        if(isSender)buildSender(button,screen,b);
        else{button.disabled=!info.available||sending||Date.now()<cooldown;button.setAttribute('aria-label','发送 '+(info.code||'未分类')+' '+(b.Label||info.name));button.addEventListener('click',()=>sendButton(screen.Id,b.Id));}
        const result=results.get(key(screen.Id,b.Id));if(result){wrap.classList.add(result.ok?'cp-sent':'cp-failed');button.title+='\n'+result.text;}
      }
      wrap.append(button);canvas.append(wrap);
    }
    if(!screen.Buttons.length)canvas.append(element('p','cp-empty',editable?'点击“＋指令按钮”开始配置，可自由拖动布局。':'本画面暂无指令按钮，请先到“面板配置”添加并关联指令。'));
    fit(canvas,screen);v.scrollLeft=scroll.left;v.scrollTop=scroll.top;setControls();
  }
  function renderMonitor(){
    if(!saved)return;remember();const screen=activeScreen();screenId=screen.Id;
    $('cp-screen-tabs').replaceChildren(...saved.Screens.map(s=>{const button=element('button','cp-screen-button'+(s.Id===screenId?' active':''),s.Name);button.type='button';button.dataset.screenId=s.Id;button.setAttribute('aria-pressed',String(s.Id===screenId));button.addEventListener('click',()=>{remember();screenId=s.Id;try{localStorage.setItem(storageKey(),screenId);}catch{}renderMonitor();const pos=positions.get(screenId);$('cp-monitor-viewport').scrollTo(pos?.left||0,pos?.top||0);});return button;}));
    draw($('cp-monitor-canvas'),screen,false);renderedScreenId=screen.Id;const pos=positions.get(screen.Id);$('cp-monitor-viewport').scrollTo(pos?.left||0,pos?.top||0);requestAnimationFrame(viewportSize);
  }
  function updateGeometry(){const b=currentButton();if(!b){setText('cp-geometry','');return;}for(const [id,prop] of geometryFields)$(id).value=b[prop];setText('cp-geometry',`${b.X}, ${b.Y} · ${b.Width}×${b.Height} px`);}
  function renderInspector(){
    const b=currentButton();if(!b){$('cp-inspector').classList.add('hidden');$('cp-properties').setAttribute('aria-expanded','false');setControls();return;}
    const info=infoFor(editScreen(),b,true);$('cp-button-label').value=b.Label;$('cp-theme').value=b.Theme;$('cp-font').value=b.FontSize;updateGeometry();
    $('cp-width').min=b.Type==='sender'?440:80;$('cp-height').min=b.Type==='sender'?300:44;
    config.querySelector('.cp-binding').classList.toggle('hidden',b.Type==='sender');$('cp-delete-button').textContent=b.Type==='sender'?'删除此发送框':'删除此按钮';
    setText('cp-binding-name',info.available?(info.code||'未分类 #'+info.number)+' · '+info.name:info.error);$('cp-binding-name').classList.toggle('cp-missing',!info.available);setText('cp-binding-destinations',(info.destinations||[]).join(', '));setControls();
  }
  function selectButton(value,open=false,event=null,preserve=false){const selected=arranger.choose(value,event,preserve);if(open&&selected){$('cp-inspector').classList.remove('hidden');$('cp-properties').setAttribute('aria-expanded','true');}return selected;}
  function renderEditor(){if(!draft)return;const screen=editScreen();editId=screen.Id;$('cp-edit-screen').replaceChildren(...draft.Screens.map(s=>new Option(s.Name,s.Id)));$('cp-edit-screen').value=screen.Id;$('cp-screen-name').value=screen.Name;draw($('cp-editor-canvas'),screen,true);renderInspector();requestAnimationFrame(viewportSize);}
  function startDrag(event,button,el,type){if(event.button!==0||saving||loading)return;event.preventDefault();event.stopPropagation();if(!selectButton(button.Id,false,type==='move'?event:null,type==='move'))return;const v=$('cp-editor-viewport');drag={button,el,type,target:event.currentTarget,x:event.clientX,y:event.clientY,left:v.scrollLeft,top:v.scrollTop,start:clone(button),group:arranger.snapshot()};drag.target.setPointerCapture(event.pointerId);arranger.sync();}
  function moveDrag(event){if(!drag||drag.target!==event.currentTarget)return;const d=drag,v=$('cp-editor-viewport'),r=v.getBoundingClientRect();if(event.clientX>r.right-20)v.scrollLeft+=12;if(event.clientX<r.left+20)v.scrollLeft-=12;if(event.clientY>r.bottom-20)v.scrollTop+=12;if(event.clientY<r.top+20)v.scrollTop-=12;const dx=event.clientX-d.x+v.scrollLeft-d.left,dy=event.clientY-d.y+v.scrollTop-d.top;if(d.type==='move')refreshGeometry(window.LayoutArrange.move(d.group,dx,dy));else{d.button.Width=Math.round(Math.max(d.button.Type==='sender'?440:80,Math.min(10000,d.start.Width+dx)));d.button.Height=Math.round(Math.max(d.button.Type==='sender'?300:44,Math.min(10000,d.start.Height+dy)));refreshGeometry([d.button]);}}
  function endDrag(event){if(!drag||drag.target!==event.currentTarget)return;try{event.currentTarget.releasePointerCapture(event.pointerId);}catch{}drag=null;setControls();}
  function adopt(data,force=false){
    const changed=!saved||saved.Revision!==data.configuration.Revision||lastLibrary!==data.libraryRevision;saved=data.configuration;lastLibrary=data.libraryRevision;bindings=new Map(data.bindings.map(b=>[key(b.screenId,b.buttonId),b]));
    if(!draft||force||!dirty){draft=clone(saved);draftBindings=new Map(bindings);dirty=false;if(!draft.Screens.some(s=>s.Id===editId))editId=draft.Screens[0].Id;setText('cp-save-state','配置已保存');}
    if(!screenId){try{screenId=localStorage.getItem(storageKey())||'';}catch{}}
    if(force||changed){if(app.view==='command-panels')renderMonitor();if(app.view==='command-panel-config'&&!dirty&&!drag)renderEditor();}setControls();
  }
  async function load(force=false){
    if(!app.user||loading||saving||sending||(dirty&&!force)||drag)return;const turn=epoch;loading=true;nextPoll=Date.now()+3000;setControls();
    try{const data=await api('command-panels');if(turn!==epoch)return;adopt(data,force);$('cp-monitor-error').textContent='';$('cp-config-error').textContent='';}
    catch(error){if(turn===epoch)$(app.view==='command-panel-config'?'cp-config-error':'cp-monitor-error').textContent=error.message;}
    finally{if(turn===epoch){loading=false;setControls();}}
  }
  async function save(){
    if(!dirty||saving||loading||!draft)return;const turn=epoch;saving=true;setControls();$('cp-config-error').textContent='';
    try{const data=await api('command-panels',{configuration:draft});if(turn!==epoch)return;adopt(data,true);toast('指令面板已保存');}
    catch(error){if(turn===epoch)$('cp-config-error').textContent=error.message;}
    finally{if(turn===epoch){saving=false;setControls();}}
  }
  async function sendButton(screen,button){
    if(!saved)return;const info=bindings.get(key(screen,button));if(!info?.available)return;
    const revision=saved.Revision;return transmit(()=>api('command-panels/preview',{revision,screenId:screen,buttonId:button}),info,key(screen,button));
  }
  async function transmit(makePlan,info,resultKey){
    if(sending||Date.now()<cooldown)return;const turn=epoch;sending=true;cooldown=Date.now()+500;setControls();setText('cp-last-result','正在发送 '+(info.code||'未分类')+' · '+info.name+'…');$('cp-last-result').className='';
    try{
      const plan=await makePlan();if(turn!==epoch)return;if(!plan.canSend)throw Error(plan.error||'发送配置不可用');
      const result=await api('command-send/send',{token:plan.token});if(turn!==epoch)return;
      const text=new Date().toLocaleTimeString('zh-CN',{hour12:false,timeZone:'Asia/Shanghai'})+' · '+(plan.code||'未分类')+' '+plan.name+' · 发送成功'+result.succeeded+'个目的IP，失败'+result.failed+'个'+(result.failed?' · '+result.rows.filter(r=>!r.success).map(r=>r.destinationIP+'：'+r.error).join('；'):'');
      results.set(resultKey,{ok:result.ok,text});setText('cp-last-result',text);$('cp-last-result').className=result.ok?'cp-result-good':'cp-result-bad';if(!result.ok)toast(text,true);
    }catch(error){if(turn===epoch){const text=(info.code||'指令')+'：'+error.message;results.set(resultKey,{ok:false,text});setText('cp-last-result',text);$('cp-last-result').className='cp-result-bad';toast(text,true);}}
    finally{if(turn===epoch){sending=false;if(app.view==='command-panels')renderMonitor();setControls();setTimeout(()=>{if(turn===epoch)setControls();},Math.max(0,cooldown-Date.now())+20);}}
  }
  async function loadPicker(){
    if(!picker.open||choosing)return;const request=++pickerRequest,turn=epoch;pickerBusy=true;setControls();$('cp-picker-error').textContent='';
    try{const data=await api('send-monitor/commands/list?page='+pickerPage+'&search='+encodeURIComponent($('cp-picker-search').value.trim())+'&kind='+encodeURIComponent($('cp-picker-kind').value));if(turn!==epoch||request!==pickerRequest||!picker.open)return;pickerPage=data.page;pickerPages=data.pages;
      $('cp-picker-rows').replaceChildren(...data.rows.map(record=>{const row=element('tr');row.dataset.code=record.code;row.dataset.number=record.number;row.append(element('td','mono',record.code||'未分类 #'+record.number),element('td','',record.name),element('td','mono small-text',record.destinations.join(', ')));const cell=element('td'),button=element('button','button small','关联');button.type='button';button.setAttribute('aria-label','关联 '+(record.code||'#'+record.number)+' '+record.name);button.addEventListener('click',()=>choose(record));cell.append(button);row.append(cell);return row;}));
      if(!data.rows.length){const row=element('tr'),cell=element('td','muted','暂无符合条件的指令，请先在指令配置中入库。');cell.colSpan=4;row.append(cell);$('cp-picker-rows').append(row);}setText('cp-picker-page','共'+data.matching+'条 · 第'+pickerPage+'/'+pickerPages+'页');
    }catch(error){if(turn===epoch&&request===pickerRequest)$('cp-picker-error').textContent=error.message;}
    finally{if(turn===epoch&&request===pickerRequest){pickerBusy=false;setControls();}}
  }
  async function choose(record){
    if(choosing||!pickerTarget)return;const turn=epoch,request=pickerRequest,target={...pickerTarget};choosing=true;setControls();
    try{const data=await api('command-panels/bind',{number:record.number,code:record.code});if(turn!==epoch||request!==pickerRequest||!picker.open||target.screen!==pickerTarget?.screen||target.button!==pickerTarget?.button)return;const b=draft.Screens.find(s=>s.Id===target.screen)?.Buttons.find(b=>b.Id===target.button);if(!b)return;for(const field of ['CommandNumber','CommandCode','CommandKind','CommandIdentity'])b[field]=data.button[field];draftBindings.set(key(target.screen,target.button),data.binding);mark();picker.close();renderEditor();}
    catch(error){if(turn===epoch&&request===pickerRequest)$('cp-picker-error').textContent=error.message;}
    finally{if(turn===epoch&&request===pickerRequest){choosing=false;setControls();}}
  }
  const geometryFields=[['cp-x','X',0,10000],['cp-y','Y',0,10000],['cp-width','Width',80,10000],['cp-height','Height',44,10000],['cp-font','FontSize',12,40]];
  function copyCommandButton(){
    const button=currentButton();if(!button||saving||loading||drag)return;
    if(window.LayoutClipboard.write('command-button',{item:button,binding:infoFor(editScreen(),button,true)}))toast('指令按钮已复制，可切换画面后粘贴');
  }
  function pasteCommandButton(){
    const screen=editScreen();if(!screen||saving||loading||drag)return;
    const value=window.LayoutClipboard.read('command-button');if(!value)return;
    if(screen.Buttons.length>=1000){toast('每个画面最多1000个指令按钮',true);return;}
    const button=value.item;Object.assign(button,window.LayoutClipboard.offset(button,screen.Buttons));button.Id=id('button');screen.Buttons.push(button);buttonId=button.Id;
    draftBindings.set(key(screen.Id,button.Id),{...value.binding,screenId:screen.Id,buttonId:button.Id});
    mark();renderEditor();$('cp-inspector').classList.add('hidden');$('cp-properties').setAttribute('aria-expanded','false');
    window.LayoutClipboard.reveal($('cp-editor-viewport'),button);toast('已粘贴指令按钮，保存全部画面后生效');
  }
  copyButton.addEventListener('click',copyCommandButton);pasteButton.addEventListener('click',pasteCommandButton);
  window.LayoutClipboard.shortcuts('command-panel-config',{copy:copyCommandButton,paste:pasteCommandButton,canCopy:()=>!!currentButton()&&!saving&&!loading&&!drag,canPaste:()=>!!editScreen()&&!saving&&!loading&&!drag&&window.LayoutClipboard.has('command-button')});
  window.addEventListener('layout-clipboard-change',setControls);
  for(const [input,field,minimum,max] of geometryFields)$(input).addEventListener('change',()=>{const b=currentButton();if(!b)return;const min=b.Type==='sender'?(field==='Width'?440:field==='Height'?300:minimum):minimum;const value=Number($(input).value);if(!Number.isFinite(value)||value<min||value>max){$('cp-config-error').textContent=`${$(input).parentElement.firstChild.textContent}需在${min}～${max}之间` ;$(input).value=b[field];return;}b[field]=Math.round(value);$('cp-config-error').textContent='';mark();renderEditor();});
  $('cp-button-label').addEventListener('change',()=>{const b=currentButton();if(b){b.Label=$('cp-button-label').value.trim();mark();renderEditor();}});
  $('cp-theme').addEventListener('change',()=>{const b=currentButton();if(b){b.Theme=$('cp-theme').value;mark();renderEditor();}});
  $('cp-screen-name').addEventListener('change',()=>{const s=editScreen(),name=$('cp-screen-name').value.trim();if(!s)return;if(!name){$('cp-screen-name').value=s.Name;$('cp-config-error').textContent='画面名称不能为空';return;}s.Name=name;mark();renderEditor();});
  $('cp-edit-screen').addEventListener('change',()=>{editId=$('cp-edit-screen').value;buttonId='';renderEditor();});
  $('cp-add-screen').addEventListener('click',()=>{if(!draft)return;if(draft.Screens.length>=20){toast('最多20个指令画面',true);return;}const screen={Id:id('screen'),Name:'画面'+(draft.Screens.length+1),Buttons:[]};draft.Screens.push(screen);editId=screen.Id;buttonId='';mark();renderEditor();});
  $('cp-delete-screen').addEventListener('click',()=>{if(!draft||draft.Screens.length<=1)return;const screen=editScreen();if(screen.Buttons.length&&!confirm('删除“'+screen.Name+'”及其中的按钮？保存后生效。'))return;draft.Screens=draft.Screens.filter(s=>s.Id!==screen.Id);for(const k of draftBindings.keys())if(k.startsWith(screen.Id+'/'))draftBindings.delete(k);editId=draft.Screens[0].Id;buttonId='';mark();renderEditor();});
  $('cp-add-button').addEventListener('click',()=>{const s=editScreen();if(!s)return;if(s.Buttons.length>=1000){toast('每个画面最多1000个指令按钮',true);return;}const n=s.Buttons.length,b={Id:id('button'),Label:'',Theme:'teal',X:24+(n%4)*224,Y:24+Math.floor(n/4)*100,Width:200,Height:76,FontSize:18,CommandNumber:0,CommandCode:'',CommandKind:'',CommandIdentity:''};s.Buttons.push(b);buttonId=b.Id;mark();renderEditor();selectButton(b.Id,true);});
  addSender.addEventListener('click',()=>{const s=editScreen();if(!s)return;if(s.Buttons.length>=1000){toast('每个画面最多1000个控件',true);return;}const box={Id:id('sender'),Type:'sender',Label:'指令发送',Theme:'teal',X:24,Y:160,Width:680,Height:460,FontSize:18,CommandNumber:0,CommandCode:'',CommandKind:'',CommandIdentity:''};s.Buttons.push(box);buttonId=box.Id;mark();renderEditor();selectButton(box.Id,true);});
  $('cp-delete-button').addEventListener('click',()=>{const s=editScreen(),b=currentButton();if(!b)return;s.Buttons=s.Buttons.filter(item=>item.Id!==b.Id);draftBindings.delete(key(s.Id,b.Id));buttonId='';mark();renderEditor();});
  $('cp-unbind').addEventListener('click',()=>{const b=currentButton();if(!b)return;b.CommandNumber=0;b.CommandCode=b.CommandKind=b.CommandIdentity='';draftBindings.delete(key(editId,b.Id));mark();renderEditor();});
  $('cp-select-command').addEventListener('click',()=>{if(!currentButton())return;pickerTarget={screen:editId,button:buttonId};pickerPage=1;picker.showModal();loadPicker();});
  for(const close of ['cp-picker-close','cp-picker-cancel'])$(close).addEventListener('click',()=>picker.close());picker.addEventListener('close',()=>{pickerRequest++;pickerTarget=null;pickerBusy=false;choosing=false;setControls();});
  $('cp-picker-query').addEventListener('click',()=>{pickerPage=1;loadPicker();});$('cp-picker-search').addEventListener('keydown',e=>{if(e.key==='Enter'){e.preventDefault();pickerPage=1;loadPicker();}});$('cp-picker-kind').addEventListener('change',()=>{pickerPage=1;loadPicker();});$('cp-picker-prev').addEventListener('click',()=>{pickerPage--;loadPicker();});$('cp-picker-next').addEventListener('click',()=>{pickerPage++;loadPicker();});
  $('cp-properties').addEventListener('click',()=>{const open=$('cp-inspector').classList.contains('hidden');$('cp-inspector').classList.toggle('hidden',!open);$('cp-properties').setAttribute('aria-expanded',String(open));});$('cp-close-properties').addEventListener('click',()=>{$('cp-inspector').classList.add('hidden');$('cp-properties').setAttribute('aria-expanded','false');});
  $('cp-reload').addEventListener('click',()=>{if(dirty&&!confirm('放弃尚未保存的指令面板修改？'))return;load(true);});$('cp-save').addEventListener('click',save);$('cp-open-config').addEventListener('click',()=>navigate('command-panel-config'));$('cp-open-panel').addEventListener('click',()=>navigate('command-panels'));
  function activate(value){if(!['command-panels','command-panel-config'].includes(value))return;if(saved){if(value==='command-panels')renderMonitor();else renderEditor();}load();requestAnimationFrame(viewportSize);}
  function render(){if(['command-panels','command-panel-config'].includes(app.view)&&Date.now()>=nextPoll&&!document.hidden)load();}
  function reset(){epoch++;pickerRequest++;saved=draft=null;bindings.clear();draftBindings.clear();results.clear();positions.clear();senders.clear();screenId=editId=buttonId='';dirty=loading=saving=sending=false;drag=null;nextPoll=cooldown=0;lastLibrary=-1;if(picker.open)picker.close();for(const id of ['cp-monitor-canvas','cp-editor-canvas','cp-screen-tabs','cp-picker-rows'])$(id).replaceChildren();$('cp-inspector').classList.add('hidden');$('cp-monitor-error').textContent=$('cp-config-error').textContent='';setText('cp-last-result','点击按钮，向该指令已配置的全部目的IP各发送一次。');setText('cp-save-state','尚未载入');setControls();}
  window.addEventListener('resize',viewportSize);window.addEventListener('beforeunload',event=>{if(app.user&&dirty){event.preventDefault();event.returnValue='';}});
  window.addEventListener('configuration-applied',()=>{nextPoll=0;if(app.view==='command-panels')load();});
  return{activate,render,reset};
})();
