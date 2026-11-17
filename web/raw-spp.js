'use strict';
window.RawSppUI=(()=>{
  names['raw-spp']=['SPP原包监视','独立留存和显示所选SPP包源码，无需启用遥测解码。'];
  const view=element('section','view hidden');view.id='view-raw-spp';
  view.innerHTML=`<section class="panel"><div class="panel-heading"><div><h2>SPP原包监视 <span id="raw-spp-count" class="count-chip">0</span></h2><p>在SPP编辑中勾选“监测原包”，随监听或文件演示接收。每个包保留最近200条，停止、重新监听及修改配置后保留，关闭程序后删除。</p></div><button id="raw-spp-config" class="button">配置SPP包</button></div><div class="section-pad"><div class="rule-toolbar"><label>SPP包筛选 <select id="raw-spp-filter" aria-label="SPP包筛选"><option value="">全部监测包</option></select></label><label class="check-label"><input id="raw-spp-follow" type="checkbox" checked>跟随最新原包</label><button id="raw-spp-refresh" class="button">刷新</button><a id="raw-spp-export" class="button" href="/api/raw-spp/export.csv" download>导出所选包CSV</a><button id="raw-spp-clear" class="text-button">清空所选包记录</button></div><p id="raw-spp-summary" class="muted small-text"></p><p id="raw-spp-error" class="form-error" role="alert"></p></div><div class="table-scroll raw-spp-table"><table class="data-table"><thead><tr><th>记录号 / 接收时间（北京时间）</th><th>SPP包 / 外层来源 / APID</th><th>来源 / 模式</th><th>帧计数 / 字节数</th><th>格式与校验</th><th>SPP源码预览</th><th>操作</th></tr></thead><tbody id="raw-spp-rows"></tbody></table></div><div class="rule-toolbar section-pad"><button id="raw-spp-prev" class="button small">上一页</button><span id="raw-spp-page"></span><button id="raw-spp-next" class="button small">下一页</button></div></section><section class="panel"><div class="panel-heading"><div><h2 id="raw-spp-detail-title">完整SPP源码</h2><p id="raw-spp-detail-note">等待接收监测原包；可点击记录查看历史源码，或跟随最新原包。</p></div></div><div class="section-pad"><p id="raw-spp-detail-error" class="form-error" role="alert"></p><label class="rule-script-label">SPP完整内容（包头至UDP末尾的实际字节）<textarea id="raw-spp-hex" class="raw-spp-source" rows="12" readonly spellcheck="false"></textarea></label><details id="raw-spp-udp-details"><summary>完整UDP载荷（含六字节外层头）</summary><textarea id="raw-spp-udp" class="raw-spp-source" rows="8" readonly spellcheck="false"></textarea></details></div></section>`;
  document.querySelector('.metric-grid').after(view);
  let generation=0,request=0,detailRequest=0,pending=false,detailPending=false,nextRefresh=0,page=1,pages=1,selectedId=0,signature='',packetId='',seenRevision=-1,seenConfig='',records=[],pressedRecord=0;
  const hex=n=>Number(n)<0?'/（仅APID）':'0x'+Number(n).toString(16).toUpperCase().padStart(4,'0');
  function empty(text){const tr=element('tr'),td=element('td','empty',text);td.colSpan=7;tr.append(td);$('raw-spp-rows').replaceChildren(tr);}
  function clearDetail(){selectedId=0;detailRequest++;$('raw-spp-detail-error').textContent='';$('raw-spp-hex').value='';$('raw-spp-udp').value='';$('raw-spp-detail-title').textContent='完整SPP源码';$('raw-spp-detail-note').textContent='等待接收监测原包；可点击记录查看历史源码，或跟随最新原包。';}
  function formatHex(value){const bytes=String(value||'').match(/\b[0-9a-f]{2}\b/gi)||[],lines=[];for(let i=0;i<bytes.length;i+=16)lines.push(bytes.slice(i,i+16).join(' ').toUpperCase());return lines.join('\n');}
  function markSelection(){for(const row of $('raw-spp-rows').children)row.classList.toggle('raw-spp-selected',Number(row.dataset.id)===selectedId);}
  async function show(id,manual=false){
    if(manual){$('raw-spp-follow').checked=false;selectedId=0;}
    if(!id||selectedId===id||detailPending&&!manual)return;
    const epoch=generation,token=++detailRequest,filter=packetId;detailPending=true;
    if(manual){$('raw-spp-detail-error').textContent='';$('raw-spp-detail-note').textContent='正在读取原包 #'+id+'，已暂停跟随最新原包。';$('raw-spp-hex').closest('section.panel').scrollIntoView({block:'start'});}
    try{const value=await api('raw-spp/detail?id='+id);if(epoch!==generation||token!==detailRequest||filter!==packetId)return;
      selectedId=id;$('raw-spp-detail-error').textContent='';$('raw-spp-hex').value=formatHex(value.sppHex);$('raw-spp-udp').value=formatHex(value.udpHex);const r=value.record;
      $('raw-spp-detail-title').textContent=r.packet+' · #'+r.id+' · 完整SPP源码';$('raw-spp-detail-note').textContent=r.time+' / '+r.source+' / 帧计数 '+r.sequence+' / SPP实际 '+r.sppLength+'字节，声明 '+r.declaredLength+'字节 / 格式'+r.status+' / Fletcher-16 '+r.checksum+(r.reason?' / '+r.reason:'')+'。'+value.note;markSelection();
    }catch(e){if(epoch===generation&&token===detailRequest)$('raw-spp-detail-error').textContent=e.message;}finally{if(token===detailRequest)detailPending=false;}
  }
  async function refresh(force=false){
    if(!app.user||pressedRecord||(pending&&!force))return;pending=true;nextRefresh=Date.now()+700;const epoch=generation,token=++request,filter=packetId,requestedPage=page;
    $('raw-spp-export').href='/api/raw-spp/export.csv?'+new URLSearchParams({packetId:filter});
    try{const value=await api('raw-spp/list?'+new URLSearchParams({packetId:filter,page:String(requestedPage)}));if(epoch!==generation||token!==request||filter!==packetId||requestedPage!==page)return;
      if(pressedRecord)return;seenRevision=value.revision;seenConfig=value.configurationRevision;page=value.page;pages=value.pages;
      const optionSignature=JSON.stringify(value.packets),selector=$('raw-spp-filter');
      if(selector.dataset.signature!==optionSignature&&document.activeElement!==selector){selector.dataset.signature=optionSignature;selector.replaceChildren(new Option('全部监测包',''),...value.packets.map(p=>new Option(p.name+' · '+hex(p.outerSourceId)+' / APID '+p.apid+' · '+p.retained+'/200'+(p.enabled?'':'（已停止监测）'),p.id)));if(filter&&!value.packets.some(p=>p.id===filter))selector.append(new Option('原筛选包（暂无记录）',filter));selector.value=filter;}
      $('raw-spp-count').textContent=value.total;$('raw-spp-summary').textContent=(filter?'当前包':'全部包')+'保留 '+value.total+'条；每个SPP包独立保留最近'+value.capacity+'条。重复帧和校验异常包也保留，格式校验不影响原文留存。';
      $('raw-spp-page').textContent=value.page+' / '+value.pages+' · '+value.total+'条';$('raw-spp-prev').disabled=page<=1;$('raw-spp-next').disabled=page>=pages;$('raw-spp-clear').disabled=value.total===0;$('raw-spp-export').textContent=filter?'导出所选包CSV':'导出全部原包CSV';
      const current=JSON.stringify([value.revision,value.configurationRevision,filter,page]);records=value.records;
      if(signature!==current){signature=current;const body=$('raw-spp-rows'),existing=new Map([...body.children].filter(row=>row.dataset.id).map(row=>[Number(row.dataset.id),row])),keep=new Set(records.map(r=>r.id));for(const row of [...body.children])if(!keep.has(Number(row.dataset.id)))row.remove();let cursor=body.firstChild;for(const r of records){let row=existing.get(r.id);if(row){if(row!==cursor)body.insertBefore(row,cursor);cursor=row.nextSibling;continue;}row=element('tr',r.status==='异常'?'raw-spp-invalid':'');row.dataset.id=r.id;
        for(const text of ['#'+r.id+'\n'+r.time,r.packet+'\n'+hex(r.outerSourceId)+' / APID '+r.apid,r.source+'\n'+r.mode+(r.captureFrame?' / 抓包#'+r.captureFrame:''),r.sequence+' / SPP '+r.sppLength+'B / UDP '+r.udpLength+'B',r.status+' / Fletcher-16 '+r.checksum+(r.reason?'\n'+r.reason:''),r.hexPreview+' …'])row.append(element('td','',text));
        const cell=element('td'),button=element('button','text-button','查看源码');button.type='button';button.dataset.recordId=r.id;button.addEventListener('click',()=>show(r.id,true));cell.append(button);row.append(cell);body.insertBefore(row,cursor);cursor=row.nextSibling;
      }if(!records.length)empty(value.packets.length?'当前筛选尚无原包记录。':'尚未勾选监测原包，请先编辑SPP配置。');markSelection();}
      $('raw-spp-error').textContent='';if($('raw-spp-follow').checked&&page===1&&records.length)show(records[0].id);
    }catch(e){if(epoch===generation&&token===request)$('raw-spp-error').textContent=e.message;}finally{if(token===request)pending=false;}
  }
  function load(){refresh(true);}
  $('raw-spp-rows').addEventListener('pointerdown',event=>{const button=event.target.closest('button[data-record-id]');if(button)pressedRecord=Number(button.dataset.recordId);});
  const releasePointer=()=>{if(!pressedRecord)return;setTimeout(()=>{pressedRecord=0;if(app.user&&app.view==='raw-spp')refresh(true);},0);};window.addEventListener('pointerup',releasePointer);window.addEventListener('pointercancel',releasePointer);window.addEventListener('blur',releasePointer);
  function render(state){if(app.view!=='raw-spp'||!app.user)return;if(Date.now()>=nextRefresh&&(state.rawSppRevision!==seenRevision||state.decoderRevision!==seenConfig))refresh();}
  function reset(){pressedRecord=0;generation++;request++;detailRequest++;pending=detailPending=false;nextRefresh=0;page=pages=1;packetId='';signature='';seenRevision=-1;seenConfig='';records=[];$('raw-spp-filter').replaceChildren(new Option('全部监测包',''));delete $('raw-spp-filter').dataset.signature;$('raw-spp-count').textContent='0';$('raw-spp-error').textContent='';$('raw-spp-summary').textContent='';$('raw-spp-page').textContent='';$('raw-spp-follow').checked=true;clearDetail();empty('等待登录');}
  $('raw-spp-filter').addEventListener('change',()=>{packetId=$('raw-spp-filter').value;page=1;signature='';clearDetail();detailPending=false;refresh(true);});
  $('raw-spp-filter').addEventListener('blur',()=>{if(app.user&&app.view==='raw-spp')refresh(true);});
  $('raw-spp-follow').addEventListener('change',()=>{if($('raw-spp-follow').checked){page=1;refresh(true);}});
  $('raw-spp-refresh').addEventListener('click',()=>refresh(true));
  $('raw-spp-prev').addEventListener('click',()=>{page=Math.max(1,page-1);$('raw-spp-follow').checked=false;refresh(true);});$('raw-spp-next').addEventListener('click',()=>{page=Math.min(pages,page+1);$('raw-spp-follow').checked=false;refresh(true);});
  $('raw-spp-clear').addEventListener('click',async()=>{const filter=packetId;if(!confirm(filter?'清空所选SPP包的原包记录？':'清空所有SPP包的原包记录？'))return;const epoch=generation;try{await api('raw-spp/clear',{packetId:filter});if(epoch!==generation)return;clearDetail();detailPending=false;page=1;signature='';await refresh(true);toast('原包记录已清空');}catch(e){if(epoch===generation)$('raw-spp-error').textContent=e.message;}});
  $('raw-spp-config').addEventListener('click',()=>navigate('decoders'));empty('等待接收监测原包');return{load,render,reset};
})();
