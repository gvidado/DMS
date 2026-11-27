'use strict';
// The query receives values only from these catalog choices; search text is never a filter value.
window.HistoryFilters=(()=>{
  const specs=[
    ['experiment','history-experiment','试验','experiments'],['session','history-session','采集会话','sessions'],
    ['namespace','history-namespace','数据流','namespaces'],['parameter','history-parameter','参数名称 / 代号','parameters'],
    ['packet','history-packet','SPP包','packets'],['command','history-command','指令名称 / 代号','commands'],
    ['direction','history-direction','通信方向','directions'],['source','history-source','来源IP','sources'],
    ['destination','history-destination','目的IP','destinations'],['status','history-status-filter','状态','statuses'],
    ['quality','history-quality','样本质量','qualities']
  ].map(([key,id,title,category])=>({key,id,title,category}));
  const selected=new Map(specs.map(s=>[s.key,new Set()]));
  let catalog={},type='samples',onChange=()=>{},active=null,draft=new Set(),page=1,searchTimer;
  const pageSize=160,$f=id=>document.getElementById(id);
  const dialog=element('dialog','parameter-dialog history-filter-dialog');dialog.id='history-filter-dialog';
  dialog.innerHTML=`<div class="dialog-heading"><div><h2 id="history-filter-title">选择查询条件</h2><p>勾选已有记录，可同时选择多项；搜索仅筛选候选项。</p></div><button id="history-filter-close" type="button" class="icon-button" aria-label="关闭查询条件选择">×</button></div><div class="selector-toolbar"><div class="search-field"><span>⌕</span><input id="history-filter-search" type="search" aria-label="搜索已有选项" placeholder="搜索名称、代号或地址" autocomplete="off"></div><select id="history-filter-stream" aria-label="候选数据流"><option value="">全部数据流</option><option>TME</option><option>TMB</option><option>TM</option></select><select id="history-filter-packet" aria-label="候选SPP包"><option value="">全部SPP包</option></select><div class="selector-actions"><button id="history-filter-select-visible" type="button" class="text-button">勾选筛选结果</button><button id="history-filter-clear" type="button" class="text-button">清空</button></div></div><div id="history-filter-options" class="catalog-options"></div><div class="history-choice-pagination"><span id="history-filter-page-note"></span><button id="history-filter-prev" type="button" class="button small">上一页</button><button id="history-filter-next" type="button" class="button small">下一页</button></div><div class="dialog-footer"><span id="history-filter-total"></span><div><button id="history-filter-cancel" type="button" class="button">取消</button><button id="history-filter-save" type="button" class="button primary">确定选择</button></div></div>`;
  document.body.append(dialog);
  const labels={RECEIVE:'接收并解码',RECEIVE_CAPTURE:'网卡接收记录',SEND_CAPTURE:'网卡发送记录',SEND_ATTEMPT:'本系统发令结果',VALID_NEW:'有效新帧',DUPLICATE:'重复帧',INVALID:'无效报文',OUT_OF_ORDER:'晚到帧',UNRECOGNIZED:'未识别SPP',CAPTURED:'已捕获',RAW_VALID:'原包校验通过',RAW_INVALID:'原包校验异常',OBSERVED:'监视到发送',SENT:'发送调用成功',SEND_FAILED:'发送调用失败',START:'开始',END:'结束'};
  function normalized(key,value){const text=String(value);if(!['experiment','session'].includes(key))return text;const raw=text.replaceAll('-','');return /^[0-9a-f]{32}$/i.test(raw)?raw.toLowerCase().replace(/^(.{8})(.{4})(.{4})(.{4})(.{12})$/,'$1-$2-$3-$4-$5'):text;}
  function choices(spec){return catalog[spec.category]||[];}
  function label(item){return labels[item.value]||item.label||String(item.value);}
  function applies(key){return !(['parameter','namespace','quality'].includes(key)&&!['samples','wide'].includes(type))&&!(key==='command'&&type!=='commands');}
  function render(spec){
    const box=$f(spec.id);if(!box)return;const values=selected.get(spec.key),items=new Map(choices(spec).map(v=>[String(v.value),v]));
    const enabled=applies(spec.key);box.classList.toggle('history-filter-inactive',!enabled);
    box.querySelector('button[data-history-select]').disabled=!enabled;
    box.querySelector('.history-filter-count').textContent=values.size?'已选 '+values.size+' 项':'全部';
    const fragment=document.createDocumentFragment();
    for(const value of values){
      const item=items.get(value)||{value,label:value};const chip=element('span','history-filter-chip');chip.title=label(item);chip.append(element('span','',label(item)));
      const remove=element('button','', '×');remove.type='button';remove.disabled=!enabled;remove.setAttribute('aria-label','移除'+label(item));remove.addEventListener('click',()=>{values.delete(value);render(spec);onChange();});chip.append(remove);fragment.append(chip);
    }
    if(!values.size)fragment.append(element('span','muted small-text',enabled?'未限定，查询全部':'此类记录不适用'));
    box.querySelector('.history-filter-selected').replaceChildren(fragment);
  }
  function mount(host,changed){
    onChange=changed||(()=>{});const fragment=document.createDocumentFragment();
    for(const spec of specs){const box=element('div','history-filter-card');box.id=spec.id;
      const heading=element('div','history-filter-card-heading');const title=element('span','',spec.title);const count=element('span','history-filter-count');heading.append(title,count);
      const button=element('button','button small','选择');button.type='button';button.dataset.historySelect=spec.key;button.setAttribute('aria-label','选择'+spec.title);button.addEventListener('click',()=>open(spec));
      const row=element('div','history-filter-card-toolbar');row.append(heading,button);const values=element('div','history-filter-selected');values.id=spec.id+'-selected';box.append(row,values);fragment.append(box);
    }
    host.replaceChildren(fragment);for(const spec of specs)render(spec);
  }
  function update(value){catalog=value||{};for(const spec of specs){const entries=new Map();for(const item of catalog[spec.category]||[]){if(!item||item.value===null||item.value===undefined)continue;const id=normalized(spec.key,item.value);entries.set(id,{...item,value:id});}catalog[spec.category]=[...entries.values()];selected.set(spec.key,new Set([...selected.get(spec.key)].map(v=>normalized(spec.key,v))));render(spec);}}
  function set(key,values){if(!selected.has(key))return;selected.set(key,new Set((values||[]).map(v=>normalized(key,v))));render(specs.find(s=>s.key===key));}
  function values(){return Object.fromEntries(specs.map(s=>[s.key,applies(s.key)?[...selected.get(s.key)]:[]]));}
  function setType(value){type=value;for(const spec of specs)render(spec);}
  function reset(){for(const spec of specs){selected.get(spec.key).clear();render(spec);}if(dialog.open)dialog.close();}
  function searchChoices(){
    if(!active)return[];const term=$f('history-filter-search').value.trim().toLocaleLowerCase(),stream=$f('history-filter-stream').value,packet=$f('history-filter-packet').value;
    return choices(active).filter(v=>{
      if(active.key==='parameter'&&stream&&(v.namespace||v.stream||String(v.value).split('.')[0])!==stream)return false;
      if(active.key==='parameter'&&packet&&!([v.packetId,...(v.packetIds||[])].includes(packet)))return false;
      return !term||[label(v),v.value,v.name,v.code,v.reference].filter(Boolean).join(' ').toLocaleLowerCase().includes(term);
    });
  }
  function total(matches){$f('history-filter-total').textContent='已选 '+draft.size+' 项 · 当前筛选 '+matches+' 项';}
  function renderChoices(){
    if(!active)return;const all=searchChoices(),pages=Math.max(1,Math.ceil(all.length/pageSize));page=Math.max(1,Math.min(page,pages));const fragment=document.createDocumentFragment();
    for(const item of all.slice((page-1)*pageSize,page*pageSize)){
      const row=element('label','catalog-option');const check=element('input');check.type='checkbox';check.value=item.value;check.checked=draft.has(item.value);
      check.addEventListener('change',()=>{if(check.checked)draft.add(item.value);else draft.delete(item.value);total(all.length);});
      const text=element('div','option-label'),name=element('div','option-name',item.name||label(item));const code=item.code||item.reference||item.value;
      if(code&&code!==name.textContent)name.append(element('span','option-code',code));
      const meta=[item.namespace,item.packetName,item.group].filter(Boolean);text.append(name);if(meta.length)text.append(element('div','option-meta',meta.join(' · ')));row.append(check,text);fragment.append(row);
    }
    if(!all.length)fragment.append(element('p','catalog-empty muted',choices(active).length?'没有匹配的候选项，请调整搜索或筛选。':'本类尚无已入库选项，可先采集数据后刷新选项。'));
    $f('history-filter-options').replaceChildren(fragment);total(all.length);$f('history-filter-page-note').textContent='第 '+page+' / '+pages+' 页';$f('history-filter-prev').disabled=page===1;$f('history-filter-next').disabled=page===pages;
  }
  function open(spec){
    active=spec;draft=new Set(selected.get(spec.key));page=1;$f('history-filter-title').textContent='选择'+spec.title;$f('history-filter-search').value='';$f('history-filter-stream').value='';
    $f('history-filter-stream').hidden=spec.key!=='parameter';$f('history-filter-packet').hidden=spec.key!=='parameter';$f('history-filter-packet').replaceChildren(new Option('全部SPP包',''),...(catalog.packets||[]).map(v=>new Option(label(v),v.value)));
    renderChoices();dialog.showModal();$f('history-filter-search').focus();
  }
  for(const id of ['history-filter-close','history-filter-cancel'])$f(id).addEventListener('click',()=>dialog.close());
  $f('history-filter-search').addEventListener('input',()=>{clearTimeout(searchTimer);searchTimer=setTimeout(()=>{page=1;renderChoices();},120);});
  for(const id of ['history-filter-stream','history-filter-packet'])$f(id).addEventListener('change',()=>{page=1;renderChoices();});
  $f('history-filter-prev').addEventListener('click',()=>{page--;renderChoices();});$f('history-filter-next').addEventListener('click',()=>{page++;renderChoices();});
  $f('history-filter-select-visible').addEventListener('click',()=>{for(const item of searchChoices())draft.add(item.value);renderChoices();});$f('history-filter-clear').addEventListener('click',()=>{draft.clear();renderChoices();});
  $f('history-filter-save').addEventListener('click',()=>{if(!active)return;set(active.key,[...draft]);dialog.close();onChange();});
  return{mount,update,set,values,setType,reset,close:()=>dialog.close()};
})();
