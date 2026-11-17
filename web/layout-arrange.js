'use strict';
window.LayoutArrange=(()=>{
  const operations=[['top','顶端对齐'],['bottom','底部对齐'],['left','左对齐'],['right','右对齐'],['horizontal','横向等距分布'],['vertical','纵向等距分布']];
  const round=value=>Math.round(value*1000)/1000;
  function plan(items,operation){
    const distribution=['horizontal','vertical'].includes(operation),minimum=distribution?3:2;
    if(!operations.some(([key])=>key===operation))return{error:'布局操作无效'};
    if(items.length<minimum)return{error:'请至少选择'+minimum+'个控件'};
    const result=items.map(item=>({item,X:item.X,Y:item.Y}));
    if(distribution){
      const axis=operation==='horizontal'?'X':'Y',size=axis==='X'?'Width':'Height';
      result.sort((a,b)=>a[axis]-b[axis]);
      const first=result[0],last=result[result.length-1],start=first[axis],end=last[axis];
      const total=result.reduce((sum,p)=>sum+p.item[size],0),gap=(end+last.item[size]-start-total)/(result.length-1);
      if(gap<-1e-7)return{error:'所选控件之间空间不足，请先拉开两端控件，再进行等距分布'};
      let position=start;
      for(const entry of result){entry[axis]=round(position);position+=entry.item[size]+Math.max(0,gap);}
      last[axis]=end;
    }else{
      const axis=['left','right'].includes(operation)?'X':'Y',size=axis==='X'?'Width':'Height';
      const far=['bottom','right'].includes(operation);
      const edge=far?Math.max(...items.map(item=>item[axis]+item[size])):Math.min(...items.map(item=>item[axis]));
      for(const entry of result)entry[axis]=round(edge-(far?entry.item[size]:0));
    }
    if(result.some(p=>!Number.isFinite(p.X)||!Number.isFinite(p.Y)||p.X<0||p.X>10000||p.Y<0||p.Y>10000))return{error:'对齐后位置超出0～10000像素，请先调整控件位置'};
    return{updates:result};
  }
  function move(snapshot,dx,dy){
    if(!snapshot.length)return[];
    dx=Math.max(-Math.min(...snapshot.map(p=>p.X)),Math.min(10000-Math.max(...snapshot.map(p=>p.X)),dx));
    dy=Math.max(-Math.min(...snapshot.map(p=>p.Y)),Math.min(10000-Math.max(...snapshot.map(p=>p.Y)),dy));
    const changed=[];
    for(const entry of snapshot){const x=round(entry.X+dx),y=round(entry.Y+dy);if(entry.item.X!==x||entry.item.Y!==y){entry.item.X=x;entry.item.Y=y;changed.push(entry.item);}}
    return changed;
  }
  const textEntry=target=>target instanceof Element&&!!target.closest('input,textarea,select,[contenteditable="true"]');
  function attach(options){
    const selected=new Set();let context,primary;
    const toolbar=element('div','layout-arrange-toolbar');toolbar.id=options.prefix+'-arrange';toolbar.setAttribute('role','group');toolbar.setAttribute('aria-label','对齐与等距分布');
    const count=element('output','layout-selection-count'),all=element('button','button small','全选'),clear=element('button','button small','取消选择');
    all.type=clear.type='button';all.id=options.prefix+'-select-all';clear.id=options.prefix+'-select-none';
    toolbar.append(count,all,clear);const controls=[];
    for(const [key,label] of operations){const button=element('button','button small',label);button.type='button';button.id=options.prefix+'-align-'+key;button.dataset.arrange=key;button.title=key==='horizontal'||key==='vertical'?'至少选中3个；保持两端控件位置，按边缘间隔等距分布':'至少选中2个；对齐到选区对应的外侧边缘';controls.push(button);toolbar.append(button);button.addEventListener('click',()=>arrange(key));}
    const hint=element('p','layout-arrange-hint','Ctrl / Shift＋单击多选；拖动或方向键整体移动。对齐不改变尺寸，等距保持两端位置；设置和复制针对当前控件。');
    options.before.before(toolbar,hint);
    function items(){return options.items()||[];}
    function sync(){
      const value=options.context();if(value!==context){context=value;selected.clear();primary=undefined;}
      const available=items(),actual=options.primary();
      if(actual!==primary){selected.clear();if(available.some(p=>p.Id===actual))selected.add(actual);primary=actual;}
      const ids=new Set(available.map(p=>p.Id));for(const id of selected)if(!ids.has(id))selected.delete(id);
      if(!selected.has(primary)){primary=[...selected].slice(-1)[0]||'';options.setPrimary(primary);}
      const blocked=options.blocked(),n=selected.size;
      const label='已选 '+n+' 个';if(count.textContent!==label)count.textContent=label;
      all.disabled=blocked||!available.length;clear.disabled=blocked||!n;
      for(const button of controls)button.disabled=blocked||n<(['horizontal','vertical'].includes(button.dataset.arrange)?3:2);
    }
    function notify(){sync();options.onSelection();}
    function choose(id,event,preserve=false){
      if(options.blocked())return false;sync();if(!items().some(p=>p.Id===id))return false;
      if(event&&(event.ctrlKey||event.metaKey||event.shiftKey)){if(selected.has(id))selected.delete(id);else selected.add(id);}
      else if(!preserve||!selected.has(id)){selected.clear();selected.add(id);}
      primary=selected.has(id)?id:[...selected].slice(-1)[0]||'';options.setPrimary(primary);notify();return selected.has(id);
    }
    function selectAll(){if(options.blocked())return;sync();for(const item of items())selected.add(item.Id);primary=selected.has(primary)?primary:items()[0]?.Id||'';options.setPrimary(primary);notify();}
    function deselect(){if(options.blocked())return;selected.clear();primary='';options.setPrimary('');notify();}
    function selection(){sync();return items().filter(p=>selected.has(p.Id));}
    function snapshot(){return selection().map(item=>({item,X:item.X,Y:item.Y}));}
    function arrange(operation){
      if(options.blocked())return;const result=plan(selection(),operation);if(result.error){options.error(result.error);return;}
      const changed=[];
      for(const entry of result.updates)if(entry.item.X!==entry.X||entry.item.Y!==entry.Y){entry.item.X=entry.X;entry.item.Y=entry.Y;changed.push(entry.item);}
      if(changed.length)options.onChange(changed);sync();
    }
    function nudge(dx,dy){if(options.blocked())return;const changed=move(snapshot(),dx,dy);if(changed.length)options.onChange(changed);}
    all.addEventListener('click',selectAll);clear.addEventListener('click',deselect);
    options.canvas.addEventListener('pointerdown',event=>{if(event.target===options.canvas)deselect();});
    document.addEventListener('keydown',event=>{
      if(app.view!==options.view||event.defaultPrevented||event.altKey||options.blocked()||textEntry(event.target)||document.querySelector('dialog[open]'))return;
      if((event.ctrlKey||event.metaKey)&&event.key.toLowerCase()==='a'){event.preventDefault();selectAll();}
      else if(event.key==='Escape'){event.preventDefault();deselect();}
    });
    return{sync,choose,selection,snapshot,nudge,isSelected:id=>selected.has(id),clear:deselect};
  }
  return{plan,move,attach};
})();
