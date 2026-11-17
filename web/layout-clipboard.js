'use strict';
// Application-local snapshots also work on offline HTTP intranets without OS clipboard permissions.
window.LayoutClipboard=(()=>{
  const prefix='telemetry.layout-clipboard.v1:',cache=new Map(),clone=value=>JSON.parse(JSON.stringify(value));
  const owner=()=>app.user?.username||'',key=type=>prefix+encodeURIComponent(owner())+':'+type;
  const finite=(value,min,max)=>Number.isFinite(value)&&value>=min&&value<=max;
  function valid(type,value){
    const item=type==='parameter-box'?value:value?.item;
    if(!item||typeof item!=='object'||!finite(item.X,0,10000)||!finite(item.Y,0,10000)||!finite(item.FontSize,12,40))return false;
    if(type==='parameter-box')return typeof item.Name==='string'&&item.Name.length>0&&item.Name.length<=40&&finite(item.Width,300,10000)&&finite(item.Height,180,10000)&&Number.isInteger(item.Columns)&&finite(item.Columns,1,6)&&finite(item.RowHeight,32,120)&&finite(item.NameWidth,80,500)&&finite(item.ValueWidth,70,500)&&['row','column'].includes(item.Order)&&['ShowUnit','ShowStatus','ShowTime','ShowHex'].every(field=>typeof item[field]==='boolean')&&Array.isArray(item.Parameters)&&item.Parameters.every(id=>typeof id==='string'&&id.length>0&&id.length<=512);
    if(type==='command-button')return [undefined,'button','sender'].includes(item.Type)&&finite(item.Width,item.Type==='sender'?440:80,10000)&&finite(item.Height,item.Type==='sender'?300:44,10000)&&typeof item.Label==='string'&&item.Label.length<=80&&['teal','blue','amber','slate'].includes(item.Theme)&&Number.isSafeInteger(item.CommandNumber)&&item.CommandNumber>=0&&typeof item.CommandCode==='string'&&item.CommandCode.length<=64&&typeof item.CommandKind==='string'&&item.CommandKind.length<=16&&typeof item.CommandIdentity==='string'&&(!item.CommandIdentity||/^[0-9A-F]{64}$/.test(item.CommandIdentity))&&value.binding&&typeof value.binding==='object'&&typeof value.binding.available==='boolean'&&Array.isArray(value.binding.destinations)&&value.binding.destinations.every(ip=>typeof ip==='string');
    return false;
  }
  function notify(type){window.dispatchEvent(new CustomEvent('layout-clipboard-change',{detail:{type}}));}
  function record(type,fresh=false){
    if(!owner())return null;const storage=key(type);if(!fresh&&cache.has(storage))return cache.get(storage);
    if(cache.get(storage)?.memoryOnly)return cache.get(storage);
    let value=null;
    try{const raw=localStorage.getItem(storage);if(raw){const data=JSON.parse(raw);if(data?.version===1&&data.owner===owner()&&data.type===type&&valid(type,data.value))value=data;}}
    catch{}
    // Keep the in-memory copy usable if browser storage is unavailable or full.
    if(!value&&cache.get(storage)?.memoryOnly)value=cache.get(storage);
    cache.set(storage,value);return value;
  }
  function write(type,value){
    if(!owner()||!valid(type,value))return false;
    const data={version:1,owner:owner(),type,value:clone(value)};
    try{localStorage.setItem(key(type),JSON.stringify(data));}catch{data.memoryOnly=true;}
    cache.set(key(type),data);notify(type);return true;
  }
  function read(type){const data=record(type,true);return data?clone(data.value):null;}
  function has(type){return !!record(type);}
  function offset(item,others){
    const occupied=new Set(others.map(value=>value.X+','+value.Y));
    const shifted=(value,delta)=>value+delta<=10000?value+delta:value-delta;
    for(let i=1;i<=others.length+1;i++){
      const column=(i-1)%100+1,row=Math.floor((i-1)/100)+column;
      const X=Math.round(shifted(item.X,column*24)),Y=Math.round(shifted(item.Y,row*24));
      if(!occupied.has(X+','+Y))return{X,Y};
    }
    return{X:item.X,Y:item.Y};
  }
  function reveal(viewport,item){
    if(item.X<viewport.scrollLeft||item.X+Math.min(item.Width,viewport.clientWidth)>viewport.scrollLeft+viewport.clientWidth)viewport.scrollLeft=Math.max(0,item.X-20);
    if(item.Y<viewport.scrollTop||item.Y+Math.min(item.Height,viewport.clientHeight)>viewport.scrollTop+viewport.clientHeight)viewport.scrollTop=Math.max(0,item.Y-20);
  }
  function shortcuts(view,actions){
    document.addEventListener('keydown',event=>{
      if(app.view!==view||event.defaultPrevented||(!event.ctrlKey&&!event.metaKey)||event.altKey||event.shiftKey||document.querySelector('dialog[open]'))return;
      const target=event.target;if(target instanceof Element&&(target.closest('input,textarea,select')||target.isContentEditable))return;
      const letter=event.key.toLowerCase();if(!['c','v'].includes(letter))return;
      if(letter==='c'&&window.getSelection()?.toString())return;
      const action=letter==='c'?actions.copy:actions.paste,allowed=letter==='c'?actions.canCopy:actions.canPaste;
      if(!allowed())return;event.preventDefault();if(!event.repeat)action();
    });
  }
  window.addEventListener('storage',event=>{
    if(event.key!==null&&!event.key.startsWith(prefix+encodeURIComponent(owner())+':'))return;
    for(const type of ['parameter-box','command-button']){cache.delete(key(type));notify(type);}
  });
  return{write,read,has,offset,reveal,shortcuts};
})();
