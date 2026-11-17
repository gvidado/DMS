'use strict';
window.ParameterDisplayUI = (() => {
  names['parameter-displays'] = ['参数显示配置', '配置显示画面及各参数框，保存后用于实时参数页面。'];
  const MIN_W=300, MIN_H=180, clone=x=>JSON.parse(JSON.stringify(x));
  let saved=null,draft=null,screenId='',editingId='',boxId='',dirty=false,busy=false,inspectorOpen=false,property='geometry',drag=null,pollAt=0,polling=false,epoch=0;
  const cells=new Map(),editorCells=new Map(),nodeText=new WeakMap(),monitorObservers=new Set(); let monitorStamp='',editorStamp='',monitorCatalog=null,editorCatalog=null,monitorScreen=null,lastRows=null,viewportFrame=0;
  let positionUser='',positionTimer=0,positionFrame=0,restoreFrame=0,restoringPosition=false;
  const monitorPositions=new Map();
  const config=element('section','view hidden');config.id='view-parameter-displays';
  config.innerHTML=`<section class="panel pd-editor-panel">
    <div class="panel-heading"><div><h2>参数画面与自由布局</h2><p>按账号保存；当前浏览器可独立切换画面。每个参数框至少300×180像素。</p></div><span id="pd-save-state" class="muted small-text">尚未载入</span></div>
    <div class="pd-toolbar"><select id="pd-edit-screen" aria-label="选择配置画面"></select><input id="pd-screen-name" maxlength="40" aria-label="画面名称"><button type="button" id="pd-add-screen" class="button small">＋画面</button><button type="button" id="pd-delete-screen" class="button small">删除画面</button><button type="button" id="pd-add-box" class="button small">＋参数框</button><button type="button" id="pd-properties" class="button small" aria-controls="pd-inspector" aria-expanded="false">参数框设置</button><button type="button" id="pd-reload" class="button small">取消修改 / 重新载入</button><button type="button" id="pd-save" class="button primary small">保存全部画面</button></div>
    <div class="pd-editor-hint"><span>拖动框头移动，拖动右下角缩放；选中框头后方向键微调，Shift为1像素。</span><output id="pd-geometry"></output></div>
    <p id="pd-error" class="form-error pd-message" role="alert"></p>
    <div class="pd-editor-stage"><div class="pd-viewport" id="pd-editor-viewport"><div id="pd-editor-canvas" class="pd-canvas pd-edit" aria-label="参数框布局画布"></div></div>
      <section id="pd-inspector" class="pd-inspector hidden" aria-label="参数框设置">
        <div class="pd-inspector-head"><h3 id="pd-inspector-title">参数框</h3><button type="button" id="pd-close-properties" class="icon-button" aria-label="收起参数框设置">×</button></div>
        <div class="pd-property-tabs"><button type="button" class="button small" data-pd-property="geometry">位置尺寸</button><button type="button" class="button small" data-pd-property="style">显示样式</button><button type="button" class="button small" data-pd-property="parameters">参数选择</button></div>
        <div data-pd-pane="geometry"><div class="pd-fields">
          <label class="pd-span">框名称<input id="pd-box-name" maxlength="40"></label>
          <label>X / px<input type="number" id="pd-x" min="0" max="10000" step="1"></label><label>Y / px<input type="number" id="pd-y" min="0" max="10000" step="1"></label>
          <label>宽度 / px<input type="number" id="pd-width" min="300" max="10000" step="1"></label><label>高度 / px<input type="number" id="pd-height" min="180" max="10000" step="1"></label>
        </div><button type="button" id="pd-delete-box" class="button small">删除此参数框</button></div>
        <div data-pd-pane="style" class="hidden"><div class="pd-fields">
          <label>列数（参数组）<input type="number" id="pd-columns" min="1" max="6" step="1"></label><label>字号 / px<input type="number" id="pd-font" min="12" max="40" step="1"></label>
          <label>行高 / px<input type="number" id="pd-row" min="32" max="120" step="1"></label><label>参数排列<select id="pd-order"><option value="column">按列排列</option><option value="row">按行排列</option></select></label>
          <label>名称列宽 / px<input type="number" id="pd-name-width" min="80" max="500" step="1"></label><label>数值列宽 / px<input type="number" id="pd-value-width" min="70" max="500" step="1"></label>
        </div><div class="pd-checks"><label><input id="pd-unit" type="checkbox">单位</label><label><input id="pd-status" type="checkbox">数据状态</label><label><input id="pd-time" type="checkbox">接收时间</label><label><input id="pd-hex" type="checkbox">16进制源码</label></div><p id="pd-row-hint" class="pd-row-hint" role="status" aria-live="polite"></p><button id="pd-row-use-minimum" type="button" class="button small">使用建议最小行高</button></div>
        <div data-pd-pane="parameters" class="hidden"><div class="pd-pick-actions"><button type="button" id="pd-select-parameters" class="button small primary">选择参数</button><button type="button" id="pd-clear-box" class="button small">清空本框参数</button></div><p id="pd-picked-count" class="muted small-text"></p><div id="pd-picked" class="pd-picked"></div></div>
      </section>
    </div>
  </section>`;
  document.querySelector('.page-footer').before(config);
  const copyButton=element('button','button small','复制参数框'),pasteButton=element('button','button small','粘贴参数框');
  copyButton.id='pd-copy-box';pasteButton.id='pd-paste-box';copyButton.type=pasteButton.type='button';
  copyButton.title='复制选中参数框（Ctrl+C）';pasteButton.title='粘贴到当前画面（Ctrl+V），支持跨画面';
  $('pd-add-box').after(copyButton,pasteButton);
  // Reception tools belong to the reception overview, including their existing IDs and handlers.
  const overview=$('view-overview'),reception=element('section','panel pd-reception-controls'),heading=element('div','panel-heading');
  $('connection').before($('listen-button'));
  heading.append(element('h2','','接入与演示'),document.querySelector('.heading-actions'));reception.append(heading);
  overview.prepend(reception,$('mode-banner'),document.querySelector('.metric-grid'));

  const currentScreen=()=>draft?.Screens.find(s=>s.Id===editingId),currentBox=()=>currentScreen()?.Boxes.find(b=>b.Id===boxId);
  const arranger=window.LayoutArrange.attach({prefix:'pd',view:'parameter-displays',before:config.querySelector('.pd-editor-hint'),canvas:$('pd-editor-canvas'),
    items:()=>currentScreen()?.Boxes,context:()=>app.user?.username+':'+editingId,primary:()=>boxId,setPrimary:id=>{boxId=id;},blocked:()=>busy||!!drag,
    onSelection:refreshSelection,onChange:refreshGeometry,error:message=>{$('pd-error').textContent=message;toast(message,true);}});
  function refreshSelection(){
    arranger.sync();for(const el of $('pd-editor-canvas').querySelectorAll('.pd-box')){el.classList.toggle('pd-selected',arranger.isSelected(el.dataset.boxId));el.classList.toggle('layout-primary',el.dataset.boxId===boxId);el.querySelector('.pd-dragbar')?.setAttribute('aria-pressed',String(arranger.isSelected(el.dataset.boxId)));}
    renderInspector();setBusy();
  }
  function refreshGeometry(items){
    if(!items.length)return;
    const nodes=new Map([...$('pd-editor-canvas').children].map(node=>[node.dataset.boxId,node]));
    for(const item of items){const node=nodes.get(item.Id);if(node)position(node,item);}
    canvasSize($('pd-editor-canvas'),currentScreen());updateGeometry();mark();
  }
  const key=()=> 'telemetry.parameter-screen.v1:'+encodeURIComponent(app.user?.username||'');
  const positionKey=()=> 'telemetry.parameter-position.v1:'+encodeURIComponent(positionUser);
  const scrollPoint=el=>({left:el.scrollLeft,top:el.scrollTop});
  function loadPositions(){
    const user=app.user?.username||'';if(positionUser===user)return;positionUser=user;monitorPositions.clear();if(!user)return;
    try{const rows=JSON.parse(sessionStorage.getItem(positionKey())||'[]');if(Array.isArray(rows))for(const row of rows.slice(0,20))if(Array.isArray(row)&&typeof row[0]==='string'&&row[1]&&typeof row[1]==='object')monitorPositions.set(row[0],row[1]);}catch{}
  }
  function persistPositions(){clearTimeout(positionTimer);positionTimer=0;if(positionUser)try{sessionStorage.setItem(positionKey(),JSON.stringify([...monitorPositions]));}catch{}}
  function rememberPosition(immediate=false){
    if(restoringPosition||app.view!=='parameters'||!monitorScreen||positionUser!==app.user?.username||!$('pd-monitor-viewport').clientWidth)return;
    const boxes=Object.create(null);for(const box of $('pd-monitor-canvas').querySelectorAll('.pd-box'))boxes[box.dataset.boxId]=scrollPoint(box.querySelector('.pd-box-body'));
    monitorPositions.set(monitorScreen.Id,{viewport:scrollPoint($('pd-monitor-viewport')),tabs:scrollPoint($('pd-screen-tabs')),boxes});
    if(immediate)persistPositions();else{clearTimeout(positionTimer);positionTimer=setTimeout(persistPositions,160);}
  }
  function schedulePositionSave(){if(restoringPosition||positionFrame)return;positionFrame=requestAnimationFrame(()=>{positionFrame=0;rememberPosition();});}
  function restorePosition(){
    if(restoreFrame)cancelAnimationFrame(restoreFrame);restoringPosition=true;const target=monitorScreen;
    const apply=()=>{if(app.view!=='parameters'||monitorScreen!==target)return;const saved=monitorPositions.get(target?.Id)||{},point=(el,value)=>{el.scrollLeft=Number.isFinite(value?.left)?Math.max(0,value.left):0;el.scrollTop=Number.isFinite(value?.top)?Math.max(0,value.top):0;};
      point($('pd-monitor-viewport'),saved.viewport);point($('pd-screen-tabs'),saved.tabs);
      for(const box of $('pd-monitor-canvas').querySelectorAll('.pd-box'))point(box.querySelector('.pd-box-body'),saved.boxes?.[box.dataset.boxId]);
    };
    viewportSize();apply();restoreFrame=requestAnimationFrame(()=>{restoreFrame=0;apply();restoringPosition=false;});
  }
  $('pd-monitor-viewport').addEventListener('scroll',schedulePositionSave,{passive:true});$('pd-screen-tabs').addEventListener('scroll',schedulePositionSave,{passive:true});
  const mark=()=>{dirty=true;setText('pd-save-state','有未保存的修改');$('pd-error').textContent='';};
  config.addEventListener('input',()=>{if(draft&&!busy)mark();});
  const uid=prefix=>prefix+'-'+(globalThis.crypto?.randomUUID?.()||Date.now().toString(36)+'-'+Math.random().toString(36).slice(2));
  function activeScreen(){return saved?.Screens.find(s=>s.Id===screenId)||saved?.Screens[0];}
  function selectedIds(value){return [...new Set(value.Screens.flatMap(s=>s.Boxes.flatMap(b=>b.Parameters)))].filter(id=>app.definitions.has(id));}
  function applyLoaded(value){
    loadPositions();rememberPosition(true);
    saved=value;draft=clone(value);dirty=false;
    let stored='';try{stored=localStorage.getItem(key())||'';}catch{}
    if(!saved.Screens.some(s=>s.Id===screenId))screenId=saved.Screens.some(s=>s.Id===stored)?stored:saved.Screens[0].Id;
    if(!draft.Screens.some(s=>s.Id===editingId))editingId=screenId;
    if(!currentBox())boxId=currentScreen()?.Boxes[0]?.Id||'';
    app.user.selected=selectedIds(saved);monitorStamp='';editorStamp='';
    setText('pd-save-state','配置已保存');$('pd-error').textContent='';
  }
  async function load(){
    const user=app.user,requestEpoch=epoch;const value=await api('parameter-displays');
    if(app.user!==user||requestEpoch!==epoch)return;applyLoaded(value);
    if(app.view==='parameter-displays')renderEditor();if(app.view==='parameters')renderMonitor();
  }
  async function poll(){
    if(polling||!saved||!app.user||!['parameters','parameter-displays'].includes(app.view)||performance.now()-pollAt<3000)return;
    pollAt=performance.now();polling=true;const user=app.user,requestEpoch=epoch,revision=saved.Revision;
    try{const value=await api('parameter-displays');if(app.user!==user||requestEpoch!==epoch||saved?.Revision!==revision||value.Revision===saved.Revision)return;
      if(dirty){$('pd-error').textContent='其他页面已更新显示配置。本页修改仍保留，请重新载入后再编辑。';return;}
      applyLoaded(value);if(app.view==='parameters')renderMonitor();else renderEditor();
    }catch{}finally{polling=false;}
  }
  function canvasSize(canvas,screen){
    if(!screen)return;const viewport=canvas.parentElement;
    const right=Math.max(0,...screen.Boxes.map(b=>b.X+b.Width)),bottom=Math.max(0,...screen.Boxes.map(b=>b.Y+b.Height));
    const width=Math.max(viewport.clientWidth,right+20)+'px',height=Math.max(viewport.clientHeight,bottom+20)+'px';if(canvas.style.width!==width)canvas.style.width=width;if(canvas.style.height!==height)canvas.style.height=height;
  }
  function scheduleViewportSize(){if(!viewportFrame)viewportFrame=requestAnimationFrame(()=>{viewportFrame=0;viewportSize();});}
  function viewportSize(){
    const viewport=app.view==='parameters'?$('pd-monitor-viewport'):app.view==='parameter-displays'?$('pd-editor-viewport'):null;if(!viewport)return;
    const height=Math.max(360,window.innerHeight-viewport.getBoundingClientRect().top-24);
    const size=height+'px';if(viewport.style.height!==size)viewport.style.height=size;if(app.view==='parameters'&&$('pd-screen-tabs').style.maxHeight!==size)$('pd-screen-tabs').style.maxHeight=size;
    canvasSize(viewport.firstElementChild,app.view==='parameters'?activeScreen():currentScreen());
  }
  function updateGeometry(){
    const b=currentBox();$('pd-geometry').textContent=b?b.Name+' · X '+b.X+'  Y '+b.Y+' · '+b.Width+' × '+b.Height+' px':'未选择参数框';
    for(const [id,key] of [['pd-x','X'],['pd-y','Y'],['pd-width','Width'],['pd-height','Height']])$(id).value=b?b[key]:'';
  }
  function position(el,b){Object.assign(el.style,{left:b.X+'px',top:b.Y+'px',width:b.Width+'px',height:b.Height+'px'});}
  function reading(id){
    const p=app.definitions.get(id),r=(latestRows||new Map()).get(id);let status=r?.status||'待接收';
    if(!p)status='参数已失配';else if(!isActive(p))status='未启用布局';
    if(app.state&&!app.connected)status='连接中断';
    const valid=p&&isActive(p);return{p,r,status,value:valid?r?.value??'—':'—',time:valid?r?.time||'—':'—',hex:valid?r?.hexSource||'':''};
  }
  let latestRows=new Map();
  function updateCell(entry){
    const {p,r,status,value,time,hex}=reading(entry.id);
    setNode(entry.value,value);if(entry.hex)setNode(entry.hex,hex?'HEX '+hex:'');if(entry.time)setNode(entry.time,time);
    if(entry.status){setNode(entry.status,status);const cls='status-tag '+statusClass(status);if(entry.status.className!==cls)entry.status.className=cls;}
    if(entry.name&&(!entry.metadataReady||entry.parameter!==p)){entry.parameter=p;entry.metadataReady=true;setNode(entry.name,p?.Name||'参数未找到');setNode(entry.code,p?parameterReference(p):entry.id);if(entry.unit)setNode(entry.unit,p?.Unit||'—');}
  }
  function setNode(el,value){const text=String(value);if(nodeText.get(el)!==text){nodeText.set(el,text);el.textContent=text;el.title=text;}}
  function buildTable(target,b,registry){
    target.replaceChildren();target.style.setProperty('--pd-font',b.FontSize+'px');target.style.setProperty('--pd-row',b.RowHeight+'px');
    if(!b.Parameters.length){target.append(element('p','pd-empty','尚未选择参数'));return;}
    const columns=[['参数名称 / 编码',b.NameWidth],['当前值',b.ValueWidth]];
    if(b.ShowUnit)columns.push(['单位',52]);if(b.ShowStatus)columns.push(['数据状态',96]);if(b.ShowTime)columns.push(['接收时间',110]);
    target.style.gridTemplateColumns='repeat('+b.Columns+',minmax('+columns.reduce((sum,c)=>sum+c[1],0)+'px,1fr))';
    const count=Math.ceil(b.Parameters.length/b.Columns);
    for(let c=0;c<b.Columns;c++){
      const table=element('table','pd-table'),cg=element('colgroup'),thead=element('thead'),hr=element('tr'),tbody=element('tbody');
      table.setAttribute('aria-label',b.Name+'第'+(c+1)+'组参数');
      for(const [i,col] of columns.entries()){const colEl=element('col');colEl.style.width=col[1]+'px';cg.append(colEl);const th=element('th',i===1?'pd-number':'',col[0]);th.scope='col';hr.append(th);}
      thead.append(hr);table.append(cg,thead,tbody);
      for(let row=0;row<count;row++){
        const id=b.Parameters[b.Order==='row'?row*b.Columns+c:c*count+row];if(!id)continue;
        const tr=element('tr'),name=element('td'),value=element('td','pd-number');tr.dataset.parameterId=id;
        const entry={id,row:tr,name:element('div','pd-name'),code:element('div','pd-code'),value:element('div','pd-value')};name.append(entry.name,entry.code);value.append(entry.value);
        if(b.ShowHex){entry.hex=element('div','pd-hex');value.append(entry.hex);}tr.append(name,value);
        if(b.ShowUnit){entry.unit=element('td','pd-unit');tr.append(entry.unit);}if(b.ShowStatus){const cell=element('td');entry.status=element('span');cell.append(entry.status);tr.append(cell);}if(b.ShowTime){entry.time=element('td','pd-time');tr.append(entry.time);}
        // A table cell's CSS height alone is only a minimum. Fixed inner viewports
        // prevent long names, engineering values or hex text from expanding a row.
        for(const cell of tr.children){
          const content=element('div','pd-cell-content');
          if(cell===entry.unit||cell===entry.time){
            const text=element('span');content.append(text);
            if(cell===entry.unit)entry.unit=text;else entry.time=text;
          }else content.append(...cell.childNodes);
          cell.append(content);
        }
        tbody.append(tr);registry.push(entry);updateCell(entry);
      }
      target.append(table);
    }
  }
  function selectBox(id,event=null,preserve=false){return arranger.choose(id,event,preserve);}
  function boxElement(b,editable,registry){
    const el=element('section','pd-box');el.dataset.boxId=b.Id;el.setAttribute('aria-label',b.Name);position(el,b);const head=element('div','pd-box-head');
    if(editable){
      const move=element('button','pd-dragbar',b.Name);move.type='button';move.setAttribute('aria-label','移动参数框 '+b.Name);head.append(move);
      const settings=element('button','pd-box-settings','设置');settings.type='button';settings.addEventListener('click',()=>{selectBox(b.Id);inspectorOpen=true;showInspector();});head.append(settings);
      const handle=element('button','pd-resize','↘');handle.type='button';handle.setAttribute('aria-label','调整参数框大小 '+b.Name);el.append(handle);
      for(const [control,type] of [[move,'move'],[handle,'resize']]){
        control.addEventListener('focus',()=>selectBox(b.Id,null,true));
        control.addEventListener('pointerdown',e=>startDrag(e,b,el,type));control.addEventListener('pointermove',moveDrag);control.addEventListener('pointerup',endDrag);control.addEventListener('pointercancel',endDrag);
        control.addEventListener('keydown',e=>{if(!['ArrowLeft','ArrowRight','ArrowUp','ArrowDown'].includes(e.key)||busy)return;e.preventDefault();selectBox(b.Id,null,type==='move');const step=e.shiftKey?1:10,dx=e.key==='ArrowLeft'?-step:e.key==='ArrowRight'?step:0,dy=e.key==='ArrowUp'?-step:e.key==='ArrowDown'?step:0;
          if(type==='move')arranger.nudge(dx,dy);else{b.Width=Math.max(MIN_W,Math.min(10000,b.Width+dx));b.Height=Math.max(MIN_H,Math.min(10000,b.Height+dy));refreshGeometry([b]);}});
      }
      el.addEventListener('pointerdown',event=>{if(!event.defaultPrevented&&event.button===0){selectBox(b.Id,event,true);if(event.ctrlKey||event.metaKey||event.shiftKey)event.preventDefault();}});
    }else head.append(element('div','pd-box-title',b.Name));
    const body=element('div','pd-box-body'),grid=element('div','pd-table-grid');buildTable(grid,b,registry);body.append(grid);el.append(head,body);
    if(!editable)body.addEventListener('scroll',schedulePositionSave,{passive:true});
    if(!editable&&typeof IntersectionObserver==='function'){
      const rows=new Map(registry.map(entry=>[entry.row,entry]));
      const observer=new IntersectionObserver(changes=>{if(!body.isConnected)return;for(const change of changes){const entry=rows.get(change.target);if(!entry)continue;entry.visible=change.isIntersecting;if(entry.visible)updateCell(entry);}},{root:body,rootMargin:'160px 0px'});
      monitorObservers.add(observer);for(const entry of registry){entry.tracked=true;observer.observe(entry.row);}
    }
    return el;
  }
  function startDrag(e,b,el,type){
    if(e.button!==0||busy)return;e.preventDefault();e.stopPropagation();if(!selectBox(b.Id,type==='move'?e:null,type==='move'))return;const viewport=$('pd-editor-viewport');
    drag={b,el,type,target:e.currentTarget,x:e.clientX,y:e.clientY,left:viewport.scrollLeft,top:viewport.scrollTop,start:clone(b),group:arranger.snapshot()};drag.target.setPointerCapture(e.pointerId);arranger.sync();
  }
  function moveDrag(e){
    if(!drag||drag.target!==e.currentTarget)return;const d=drag,v=$('pd-editor-viewport'),r=v.getBoundingClientRect();
    if(e.clientX>r.right-25)v.scrollLeft+=12;if(e.clientX<r.left+25)v.scrollLeft-=12;if(e.clientY>r.bottom-25)v.scrollTop+=12;if(e.clientY<r.top+25)v.scrollTop-=12;
    const dx=e.clientX-d.x+v.scrollLeft-d.left,dy=e.clientY-d.y+v.scrollTop-d.top;
    if(d.type==='move')refreshGeometry(window.LayoutArrange.move(d.group,dx,dy));
    else{d.b.Width=Math.round(Math.max(MIN_W,Math.min(10000,d.start.Width+dx)));d.b.Height=Math.round(Math.max(MIN_H,Math.min(10000,d.start.Height+dy)));refreshGeometry([d.b]);}
  }
  function endDrag(e){if(!drag||drag.target!==e.currentTarget)return;try{e.currentTarget.releasePointerCapture(e.pointerId);}catch{}drag=null;setBusy();}
  function draw(canvas,screen,editable){
    if(!editable){for(const observer of monitorObservers)observer.disconnect();monitorObservers.clear();}
    const registry=editable?editorCells:cells;registry.clear();canvas.replaceChildren();
    if(!screen)return;
    for(const b of screen.Boxes){const entries=[];canvas.append(boxElement(b,editable,entries));registry.set(b.Id,entries);}
    if(!screen.Boxes.length)canvas.append(element('p','pd-empty',editable?'点击“＋参数框”开始配置':'本画面暂无参数框，请在系统配置的“参数显示配置”中添加。'));
    canvasSize(canvas,screen);if(editable)refreshSelection();
  }
  function renderMonitor(){
    if(!saved)return;const screen=activeScreen(),stamp=saved.Revision;
    if(stamp!==monitorStamp||monitorCatalog!==app.catalog||monitorScreen!==screen){loadPositions();rememberPosition(true);restoringPosition=true;monitorStamp=stamp;monitorCatalog=app.catalog;monitorScreen=screen;const tabs=$('pd-screen-tabs');tabs.replaceChildren();for(const s of saved.Screens){const button=element('button','pd-screen-button'+(s.Id===screen.Id?' active':''),s.Name);button.type='button';button.dataset.screenId=s.Id;button.setAttribute('aria-pressed',String(s.Id===screen.Id));button.addEventListener('click',()=>{rememberPosition(true);screenId=s.Id;try{localStorage.setItem(key(),s.Id);}catch{}monitorStamp='';renderMonitor();viewportSize();});tabs.append(button);}draw($('pd-monitor-canvas'),screen,false);restorePosition();}
    for(const entries of cells.values())for(const entry of entries)if(!entry.tracked||entry.visible!==false)updateCell(entry);scheduleViewportSize();
  }
  function renderEditor(){
    if(!draft)return;$('pd-edit-screen').replaceChildren(...draft.Screens.map(s=>new Option(s.Name,s.Id)));$('pd-edit-screen').value=editingId;$('pd-screen-name').value=currentScreen().Name;
    draw($('pd-editor-canvas'),currentScreen(),true);editorCatalog=app.catalog;editorStamp='ready';scheduleViewportSize();
  }
  function showInspector(){$('pd-inspector').classList.toggle('hidden',!inspectorOpen||!currentBox());$('pd-properties').setAttribute('aria-expanded',String(inspectorOpen&&!!currentBox()));}
  function renderPicked(){
    const b=currentBox();if(!b)return;setText('pd-picked-count','本框已选 '+b.Parameters.length+' 项 · 不设数量上限');$('pd-picked').replaceChildren(...b.Parameters.map(id=>{const p=app.definitions.get(id),row=element('div',p?'':'pd-missing'),label=element('span','',p?parameterReference(p)+' · '+p.Name:'参数失配 · '+id),remove=element('button','text-button','移除');remove.type='button';remove.setAttribute('aria-label','移除参数 '+(p?parameterReference(p):id));remove.disabled=busy;remove.addEventListener('click',()=>mutate(()=>{b.Parameters=b.Parameters.filter(value=>value!==id);renderPicked();refreshBox();}));row.append(label,remove);return row;}));
  }
  const fields=[['pd-x','X',0,10000],['pd-y','Y',0,10000],['pd-width','Width',MIN_W,10000],['pd-height','Height',MIN_H,10000],['pd-columns','Columns',1,6],['pd-font','FontSize',12,40],['pd-row','RowHeight',32,120],['pd-name-width','NameWidth',80,500],['pd-value-width','ValueWidth',70,500]];
  const flags=[['pd-unit','ShowUnit'],['pd-status','ShowStatus'],['pd-time','ShowTime'],['pd-hex','ShowHex']];
  // Measure display typography only on configuration edits, never on telemetry updates.
  const rowMetrics=element('table','pd-table pd-row-metrics'),metricRow=element('tr'),metricBody=element('tbody');
  rowMetrics.setAttribute('aria-hidden','true');
  const metricColumns=[
    {key:'name',nodes:[element('div','pd-name','参数名称'),element('div','pd-code','TME.A1')]},
    {key:'value',nodes:[element('div','pd-value','9.79'),element('div','pd-hex','HEX 00 00')]},
    {key:'ShowUnit',nodes:[element('span','pd-unit','单位')]},
    {key:'ShowStatus',nodes:[element('span','status-tag','有效')]},
    {key:'ShowTime',nodes:[element('span','pd-time','12:34:56.789')]}
  ];
  for(const column of metricColumns){column.cell=element('td');column.content=element('div','pd-cell-content');column.content.append(...column.nodes);column.cell.append(column.content);metricRow.append(column.cell);}
  metricBody.append(metricRow);rowMetrics.append(metricBody);document.body.append(rowMetrics);
  $('pd-row').setAttribute('aria-describedby','pd-row-hint');
  function minimumRowHeight(style){
    rowMetrics.style.setProperty('--pd-font',style.FontSize+'px');
    for(const column of metricColumns)column.cell.classList.toggle('hidden',column.key.startsWith('Show')&&!style[column.key]);
    metricColumns[1].nodes[1].classList.toggle('hidden',!style.ShowHex);
    const content=Math.max(...metricColumns.filter(column=>!column.cell.classList.contains('hidden')).map(column=>column.content.getBoundingClientRect().height));
    return Math.min(120,Math.max(32,Math.ceil(content+9)));
  }
  function rowHeightRecommendation(){
    const box=currentBox();if(!box)return null;
    const size=Number($('pd-font').value),style={...box,FontSize:Number.isFinite(size)&&size>=12&&size<=40?size:box.FontSize};
    for(const [id,key] of flags)style[key]=$(id).checked;
    return{minimum:minimumRowHeight(style),font:style.FontSize};
  }
  function updateRowHeightHint(){
    const recommendation=rowHeightRecommendation();if(!recommendation)return;
    const current=Number($('pd-row').value),small=Number.isFinite(current)&&current<recommendation.minimum;
    setText('pd-row-hint','建议最小行高：'+recommendation.minimum+' px。按当前 '+recommendation.font+' px 字号和勾选显示项计算，含上下留白。'+(small?' 当前行高偏小，名称、数值或源码可能被裁切。':''));
    $('pd-row-hint').classList.toggle('pd-row-warning',small);
    $('pd-row-use-minimum').disabled=busy;
  }
  function renderInspector(){
    showInspector();const b=currentBox();updateGeometry();if(!b)return;$('pd-inspector-title').textContent=b.Name;$('pd-box-name').value=b.Name;
    for(const [id,key] of fields)$(id).value=b[key];for(const [id,key] of flags)$(id).checked=b[key];$('pd-order').value=b.Order;renderPicked();updateRowHeightHint();
    config.querySelectorAll('[data-pd-pane]').forEach(p=>p.classList.toggle('hidden',p.dataset.pdPane!==property));config.querySelectorAll('[data-pd-property]').forEach(b=>b.classList.toggle('primary',b.dataset.pdProperty===property));
  }
  function refreshBox(){const b=currentBox();if(!b)return;updateRowHeightHint();const el=[...$('pd-editor-canvas').children].find(el=>el.dataset.boxId===b.Id);if(!el)return;const entries=[];buildTable(el.querySelector('.pd-table-grid'),b,entries);editorCells.set(b.Id,entries);}
  function mutate(action,redraw=false){if(!draft||busy)return;action();mark();if(redraw)renderEditor();}
  function copyBox(){
    const box=currentBox();if(!box||busy||drag)return;
    if(window.LayoutClipboard.write('parameter-box',box))toast('参数框已复制，可切换画面后粘贴');
  }
  function pasteBox(){
    const screen=currentScreen();if(!screen||busy||drag)return;
    const value=window.LayoutClipboard.read('parameter-box');if(!value)return;
    if(screen.Boxes.length>=40){toast('每个画面最多40个参数框',true);return;}
    const base=value.Name;let counter=1,name;
    do{const suffix=' 副本'+(counter===1?'':counter);name=base.slice(0,40-suffix.length)+suffix;counter++;}while(screen.Boxes.some(box=>box.Name===name));
    mutate(()=>{Object.assign(value,window.LayoutClipboard.offset(value,screen.Boxes));value.Id=uid('box');value.Name=name;screen.Boxes.push(value);boxId=value.Id;inspectorOpen=false;},true);
    window.LayoutClipboard.reveal($('pd-editor-viewport'),value);toast('已粘贴参数框，保存全部画面后生效');
  }
  copyButton.addEventListener('click',copyBox);pasteButton.addEventListener('click',pasteBox);
  window.LayoutClipboard.shortcuts('parameter-displays',{copy:copyBox,paste:pasteBox,canCopy:()=>!!currentBox()&&!busy&&!drag,canPaste:()=>!!currentScreen()&&!busy&&!drag&&window.LayoutClipboard.has('parameter-box')});
  window.addEventListener('layout-clipboard-change',setBusy);
  for(const [id,key,min,max] of fields)$(id).addEventListener('change',()=>{const b=currentBox();if(!b||busy)return;const input=Number($(id).value);if(!Number.isFinite(input))return;mutate(()=>{b[key]=Math.round(Math.min(max,Math.max(min,input)));$(id).value=b[key];const el=[...$('pd-editor-canvas').children].find(el=>el.dataset.boxId===b.Id);position(el,b);canvasSize($('pd-editor-canvas'),currentScreen());updateGeometry();if(!['X','Y','Width','Height'].includes(key))refreshBox();});});
  for(const [id,key] of flags)$(id).addEventListener('change',()=>mutate(()=>{const b=currentBox();if(b){b[key]=$(id).checked;refreshBox();}}));
  for(const id of ['pd-font','pd-row'])$(id).addEventListener('input',updateRowHeightHint);
  $('pd-row-use-minimum').addEventListener('click',()=>{
    if(busy)return;const recommendation=rowHeightRecommendation();if(!recommendation)return;
    $('pd-row').value=recommendation.minimum;$('pd-row').dispatchEvent(new Event('change'));
  });
  $('pd-order').addEventListener('change',()=>mutate(()=>{const b=currentBox();if(b){b.Order=$('pd-order').value;refreshBox();}}));
  $('pd-box-name').addEventListener('change',()=>mutate(()=>{const b=currentBox();if(b)b.Name=$('pd-box-name').value.trim()||'参数框';},true));
  $('pd-screen-name').addEventListener('change',()=>mutate(()=>{currentScreen().Name=$('pd-screen-name').value.trim()||'画面';},true));
  config.querySelectorAll('[data-pd-property]').forEach(button=>button.addEventListener('click',()=>{property=button.dataset.pdProperty;renderInspector();}));
  $('pd-properties').addEventListener('click',()=>{inspectorOpen=!inspectorOpen;showInspector();});$('pd-close-properties').addEventListener('click',()=>{inspectorOpen=false;showInspector();});
  $('pd-edit-screen').addEventListener('change',()=>{editingId=$('pd-edit-screen').value;boxId=currentScreen().Boxes[0]?.Id||'';inspectorOpen=false;renderEditor();});
  $('pd-add-screen').addEventListener('click',()=>{if(draft.Screens.length>=20)return toast('最多配置20个画面',true);mutate(()=>{const s={Id:uid('screen'),Name:'画面'+(draft.Screens.length+1),Boxes:[]};draft.Screens.push(s);editingId=s.Id;boxId='';inspectorOpen=false;},true);});
  $('pd-delete-screen').addEventListener('click',()=>{if(draft.Screens.length===1)return toast('至少保留一个画面',true);if(!confirm('删除当前画面及其中全部参数框？保存后生效。'))return;mutate(()=>{draft.Screens=draft.Screens.filter(s=>s.Id!==editingId);editingId=draft.Screens[0].Id;boxId=currentScreen().Boxes[0]?.Id||'';},true);});
  $('pd-add-box').addEventListener('click',()=>{const s=currentScreen();if(!s)return;if(s.Boxes.length>=40)return toast('每个画面最多40个参数框',true);mutate(()=>{const n=s.Boxes.length,b={Id:uid('box'),Name:'参数框'+(n+1),X:20+(n%5)*30,Y:20+(n%5)*35,Width:Math.max(MIN_W,Math.min(520,$('pd-editor-viewport').clientWidth-40)),Height:360,Columns:1,FontSize:18,RowHeight:54,NameWidth:140,ValueWidth:110,Order:'column',ShowUnit:true,ShowStatus:true,ShowTime:false,ShowHex:true,Parameters:[]};s.Boxes.push(b);boxId=b.Id;inspectorOpen=true;},true);});
  $('pd-delete-box').addEventListener('click',()=>{if(!currentBox()||!confirm('删除此参数框？保存后生效。'))return;mutate(()=>{const s=currentScreen();s.Boxes=s.Boxes.filter(b=>b.Id!==boxId);boxId=s.Boxes[0]?.Id||'';},true);});
  $('pd-select-parameters').addEventListener('click',()=>{const b=currentBox();if(!b||busy)return;const target=b;openSelector(b.Parameters,ids=>{if(!draft?.Screens.some(s=>s.Boxes.includes(target)))return;target.Parameters=ids;mark();renderPicked();refreshBox();});});
  $('pd-clear-box').addEventListener('click',()=>{const b=currentBox();if(!b||!b.Parameters.length)return;if(!confirm('清空本参数框中已选的参数？保存后生效。'))return;mutate(()=>{b.Parameters=[];renderPicked();refreshBox();});});
  $('pd-save').addEventListener('click',async()=>{
    if(!draft||busy)return;busy=true;setBusy();const user=app.user,requestEpoch=epoch;
    try{const value=await api('parameter-displays',{configuration:clone(draft)});if(app.user!==user||requestEpoch!==epoch)return;applyLoaded(value);renderEditor();toast('全部参数画面已保存');}
    catch(e){if(app.user===user)$('pd-error').textContent=e.message;}finally{busy=false;setBusy();}
  });
  function setBusy(){for(const control of config.querySelectorAll('button,input,select'))control.disabled=busy;copyButton.disabled=busy||!currentBox();pasteButton.disabled=busy||!currentScreen()||!window.LayoutClipboard.has('parameter-box');arranger.sync();}
  $('pd-reload').addEventListener('click',async()=>{if(busy||(dirty&&!confirm('放弃本页未保存的修改，并重新载入？')))return;try{await load();toast('已载入保存的显示配置');}catch(e){$('pd-error').textContent=e.message;}});
  function render(){
    const rows=app.state?.rows;if(rows!==lastRows){lastRows=rows;latestRows=new Map((rows||[]).map(r=>[r.id,r]));}
    if(app.view==='parameters')renderMonitor();
    if(app.view==='parameter-displays'){
      if(!editorStamp||editorCatalog!==app.catalog)renderEditor();
      for(const entries of editorCells.values())for(const entry of entries)updateCell(entry);
    }poll();
  }
  function activate(view){if(view==='parameters'){renderMonitor();restorePosition();}if(view==='parameter-displays')renderEditor();scheduleViewportSize();}
  function reset(){rememberPosition(true);persistPositions();for(const observer of monitorObservers)observer.disconnect();monitorObservers.clear();if(viewportFrame)cancelAnimationFrame(viewportFrame);if(positionFrame)cancelAnimationFrame(positionFrame);if(restoreFrame)cancelAnimationFrame(restoreFrame);viewportFrame=positionFrame=restoreFrame=0;restoringPosition=false;positionUser='';monitorPositions.clear();monitorCatalog=editorCatalog=monitorScreen=lastRows=null;latestRows=new Map();epoch++;saved=draft=null;screenId=editingId=boxId='';monitorStamp=editorStamp='';dirty=false;busy=false;polling=false;pollAt=0;inspectorOpen=false;drag=null;cells.clear();editorCells.clear();$('pd-monitor-canvas').replaceChildren();$('pd-editor-canvas').replaceChildren();$('pd-screen-tabs').replaceChildren();$('pd-inspector').classList.add('hidden');app.selectionTarget=null;setBusy();if($('parameter-dialog').open)$('parameter-dialog').close();}
  window.addEventListener('resize',scheduleViewportSize);
  window.addEventListener('beforeunload',event=>{rememberPosition(true);persistPositions();if(app.user&&dirty){event.preventDefault();event.returnValue='';}});
  window.addEventListener('pagehide',()=>{rememberPosition(true);persistPositions();});
  return{load,render,activate,reset,rememberPosition};
})();
