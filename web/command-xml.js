'use strict';
window.CommandXmlUI=(()=>{
  const panel=element('details','import-details');panel.id='command-xml-panel';panel.open=true;
  panel.innerHTML=`<summary>从XML文件导入STC控制指令</summary>
    <p class="muted small-text">仅读取STCVars中的STCVar：名称取name，功能ID取bujianid（2字节），接口类型取gongnengid（功能ID为0xAA/170时2字节，其余4字节），数据内容区取datastr。数字默认十进制，支持0x；多字节字段低字节在前。每项作为单包，自动计算长度和累加和，仅入库，不发送。</p>
    <form id="command-xml-form"><div class="form-grid">
      <label class="file-field">选择XML文件<input id="command-xml-file" type="file" accept=".xml" required></label>
      <label>默认类别<select id="command-xml-kind"><option>ZK</option><option>BC</option><option>KC</option><option>PSS</option></select></label>
      <label>默认目的IP<input id="command-xml-destination" value="192.168.1.111" required><small>ZK默认.111，BC默认.113，KC默认.137，PSS默认.111；可修改。</small></label>
      <label>起始编号（可留空）<input id="command-xml-code" maxlength="32" placeholder="自动顺序编号，如ZK0001"><small>ZK/BC/KC至少4位数字，PSS至少3位。</small></label>
      <label>帧计数<input id="command-xml-counter" value="0x00AA" required><small>XML未提供该字段，默认沿用已确认样例值；不自动累加。</small></label>
    </div><div class="import-action-line"><button id="command-xml-read" class="button" type="submit">读取并预览XML</button><button id="command-xml-cancel" class="button" type="button">取消</button><button id="command-xml-clear" class="button" type="button">清空</button></div></form>
    <p class="muted small-text">datastr为空、仅含空白或缺少该属性时直接跳过，不入库、不占用编号。XML和手动STC均先检查数据区是否完整包含库内BC令的数据区，命中则跳过并显示BC编号。ZK通过CSV入库时也执行此检查。不占用新编号、不修改已有BC；其余按完整UDP源码判重。</p>
    <p id="command-xml-error" class="form-error" role="alert"></p>
    <div id="command-xml-preview" class="hidden"><div id="command-xml-summary" class="import-summary"></div><div id="command-xml-errors" class="form-error"></div>
      <p class="muted small-text">可逐项修改类别、编号、名称和目的IP，修改后请重新检测。点击行查看完整UDP载荷及数据内容区。</p>
      <div class="table-scroll"><table class="data-table"><thead><tr><th>STC项 / XML行</th><th>类别</th><th>编号</th><th>指令名称</th><th>目的IP</th><th>功能ID / 接口类型</th><th>UDP字节数</th><th>状态 / BC匹配 / 错误</th></tr></thead><tbody id="command-xml-rows"></tbody></table></div>
      <div class="current-table-pagination"><span id="command-xml-page"></span><div><button id="command-xml-previous" class="button small">上一页</button> <button id="command-xml-next" class="button small">下一页</button></div></div>
      <div class="command-code-columns"><label>完整UDP源码<textarea id="command-xml-hex" class="mono" rows="5" readonly spellcheck="false"></textarea></label><label>数据内容区（datastr）<textarea id="command-xml-data" class="mono" rows="5" readonly spellcheck="false"></textarea></label></div>
      <div class="import-action-line"><button id="command-xml-recheck" class="button">重新检测</button><button id="command-xml-apply" class="button primary" disabled>确认入库</button><span id="command-xml-dirty" class="muted small-text"></span></div>
    </div>`;
  $('command-bin-form').closest('details').after(panel);
  let preview=null,busy=false,reading=false,dirty=false,generation=0,controller=null,page=1;
  const defaults={ZK:'192.168.1.111',BC:'192.168.1.113',KC:'192.168.1.137',PSS:'192.168.1.111'};
  function revoke(token){if(token)api('send-monitor/commands/preview/cancel',{token}).catch(()=>{});}
  function controls(){
    for(const id of ['command-xml-file','command-xml-kind','command-xml-destination','command-xml-code','command-xml-counter'])$(id).disabled=busy&&!reading;$('command-xml-read').disabled=busy;$('command-xml-recheck').disabled=busy||!preview;
    $('command-xml-apply').disabled=busy||dirty||!preview?.canApply;
    $('command-xml-cancel').disabled=busy&&!reading;$('command-xml-clear').disabled=busy&&!reading;
    for(const input of $('command-xml-rows').querySelectorAll('input,select'))input.disabled=busy||input.dataset.skipped==='yes';
    $('command-xml-dirty').textContent=dirty?'预览已修改，请重新检测后入库。':'';
  }
  function cancel(notify=false){
    generation++;controller?.abort();controller=null;revoke(preview?.token);preview=null;busy=reading=dirty=false;page=1;
    $('command-xml-preview').classList.add('hidden');$('command-xml-error').textContent='';$('command-xml-errors').replaceChildren();$('command-xml-rows').replaceChildren();
    $('command-xml-hex').value='';$('command-xml-data').value='';controls();if(notify)toast('已取消XML读取，指令库未修改');
  }
  function markDirty(){dirty=true;controls();}
  function showSource(row){$('command-xml-hex').value=row.packetHex||'';$('command-xml-data').value=row.dataHex||'';}
  function renderRows(){
    const rows=preview?.xmlRows||[],pages=Math.max(1,Math.ceil(rows.length/50));page=Math.max(1,Math.min(page,pages));
    const fragment=document.createDocumentFragment();
    for(const item of rows.slice((page-1)*50,page*50)){
      const row=element('tr');row.dataset.xmlIndex=item.index;row.append(element('td','mono small-text',item.index+' / '+item.line));
      for(const field of ['kind','code','name','destination']){
        const cell=element('td'),input=field==='kind'?element('select'):element('input');input.dataset.field=field;
        if(field==='kind')for(const kind of Object.keys(defaults))input.append(new Option(kind,kind));
        input.value=item[field]||'';input.setAttribute('aria-label','STC第'+item.index+'项'+({kind:'类别',code:'编号',name:'名称',destination:'目的IP'})[field]);
        if(item.status.endsWith('跳过')){input.disabled=true;input.dataset.skipped='yes';}
        input.addEventListener(field==='kind'?'change':'input',()=>{
          item[field]=input.value;
          if(field==='kind'){
            const digits=(item.code||'').replace(/^[A-Za-z]+/,'');item.code=digits?item.kind+digits.replace(/^0+(?=\d)/,'').padStart(item.kind==='PSS'?3:4,'0'):'';
            item.destination=defaults[item.kind];row.querySelector('[data-field="code"]').value=item.code;row.querySelector('[data-field="destination"]').value=item.destination;
          }
          markDirty();
        });cell.append(input);row.append(cell);
      }
      row.append(element('td','mono small-text',item.functionID+' / '+item.interfaceType+(item.interfaceBytes?'（'+item.interfaceBytes+'字节）':'')),element('td','mono',item.payloadLength||'—'));
      const state=element('td','small-text');state.append(element('span','status-tag '+(item.errors.length?'bad':item.status.endsWith('跳过')?'neutral':'good'),item.status));
      for(const message of [...item.matches,...item.errors])state.append(element('p','small-text',message));row.append(state);row.addEventListener('click',()=>showSource(item));fragment.append(row);
    }
    $('command-xml-rows').replaceChildren(fragment);$('command-xml-page').textContent='第'+page+'/'+pages+'页 · '+rows.length+'项';
    $('command-xml-previous').disabled=page<=1;$('command-xml-next').disabled=page>=pages;controls();
  }
  function show(data){
    preview=data;dirty=false;$('command-xml-preview').classList.remove('hidden');
    $('command-xml-summary').textContent='读取STC '+data.total+'项 · 待新增'+data.addedCommands+'份 · 数据为空，跳过'+(data.skippedEmpty||0)+'项 · BC数据重复，跳过'+(data.skippedBC||0)+'项 · 完整UDP相同，跳过'+(data.skippedExisting??data.skipped)+'项 · 错误'+data.errors.length+'项';
    $('command-xml-errors').replaceChildren(...(data.warnings||[]).map(message=>element('p','muted',message)),...data.errors.map(message=>element('p','',message)));
    renderRows();if(data.xmlRows?.length)showSource(data.xmlRows[0]);
  }
  for(const id of ['command-xml-file','command-xml-kind','command-xml-destination','command-xml-code','command-xml-counter'])$(id).addEventListener(id.endsWith('file')||id.endsWith('kind')?'change':'input',()=>cancel());
  $('command-xml-kind').addEventListener('change',()=>{
    const kind=$('command-xml-kind').value;$('command-xml-destination').value=defaults[kind];
    const value=$('command-xml-code').value.trim(),digits=value.replace(/^[A-Za-z]+/,'');
    if(value&&/^\d+$/.test(digits))$('command-xml-code').value=kind+digits.replace(/^0+(?=\d)/,'').padStart(kind==='PSS'?3:4,'0');
  });
  $('command-xml-form').addEventListener('submit',async event=>{
    event.preventDefault();if(busy)return;const file=$('command-xml-file').files[0];if(!file){$('command-xml-error').textContent='请选择XML文件';return;}
    window.SendMonitorUI?.discardImports();cancel();const epoch=++generation;busy=reading=true;controller=new AbortController();controls();
    try{
      const query=new URLSearchParams({format:'stc-xml',kind:$('command-xml-kind').value,destinationIP:$('command-xml-destination').value.trim(),code:$('command-xml-code').value.trim(),frameCounter:$('command-xml-counter').value.trim()});
      const response=await fetch('/api/send-monitor/commands/preview?'+query,{method:'POST',credentials:'same-origin',headers:{'Content-Type':'application/xml','X-CSRF-Token':app.user.csrf},body:file,signal:controller.signal});
      const data=await response.json();if(epoch!==generation){revoke(data.token);return;}if(!response.ok)throw new Error(data.error||'XML读取失败');show(data);
    }catch(error){if(epoch===generation&&error.name!=='AbortError')$('command-xml-error').textContent=error.message;}
    finally{if(epoch===generation){busy=reading=false;controller=null;controls();}}
  });
  $('command-xml-recheck').addEventListener('click',async()=>{
    if(busy||!preview)return;const epoch=generation;busy=true;controls();$('command-xml-error').textContent='';
    try{const data=await api('send-monitor/commands/stc-recheck',{token:preview.token,rows:preview.xmlRows.map(({index,name,kind,code,destination})=>({index,name,kind,code,destination}))});if(epoch===generation)show(data);}
    catch(error){if(epoch===generation)$('command-xml-error').textContent=error.message;}
    finally{if(epoch===generation){busy=false;controls();}}
  });
  $('command-xml-apply').addEventListener('click',async()=>{
    if(busy||dirty||!preview?.canApply)return;const epoch=generation;busy=true;controls();$('command-xml-error').textContent='';
    try{await api('send-monitor/commands/apply',{token:preview.token});if(epoch!==generation)return;preview=null;$('command-xml-preview').classList.add('hidden');await window.SendMonitorUI?.loadCatalog();toast('STC指令已入库；空数据及完整源码重复的项目已跳过');}
    catch(error){if(epoch===generation)$('command-xml-error').textContent=error.message;}
    finally{if(epoch===generation){busy=false;controls();}}
  });
  $('command-xml-cancel').addEventListener('click',()=>cancel(true));
  $('command-xml-clear').addEventListener('click',()=>{cancel();$('command-xml-form').reset();toast('已清空XML读取内容，指令库未修改');});
  $('command-xml-previous').addEventListener('click',()=>{page--;renderRows();});$('command-xml-next').addEventListener('click',()=>{page++;renderRows();});
  controls();return{cancel,reset(){cancel();$('command-xml-form').reset();}};
})();

window.CommandManualUI=(()=>{
  const panel=element('details','import-details');panel.id='command-manual-panel';panel.open=true;
  panel.innerHTML=`<summary>手动添加ZK / STC指令</summary><p class="muted small-text">与XML导入采用相同封装：部件/功能ID为bujianid（2字节），接口类型为gongnengid（功能ID为0xAA/170时2字节，其余4字节），数据内容为datastr。数字默认十进制，可填0x十六进制；多字节字段低字节在前。预览、入库不发送指令。</p>
    <form id="command-manual-form"><div class="form-grid">
      <label>指令名称（可选）<input id="command-manual-name" maxlength="256" placeholder="留空使用“手动ZK指令”"></label>
      <label>类别<select id="command-manual-kind"><option>ZK</option><option>BC</option><option>KC</option><option>PSS</option></select></label>
      <label>指令编号（可留空）<input id="command-manual-code" maxlength="32" placeholder="自动顺序编号，如ZK0001"></label>
      <label>目的IP<input id="command-manual-destination" value="192.168.1.111" required></label>
      <label>部件/功能ID（bujianid，2字节）<input id="command-manual-part" placeholder="例如170或0x00AA" required></label>
      <label><span id="command-manual-interface-label">接口类型（gongnengid，4字节）</span><input id="command-manual-interface" placeholder="例如92或0x0000005C" required></label>
    </div><label>数据内容（datastr，十六进制）<textarea id="command-manual-data" class="mono" rows="4" maxlength="400000" placeholder="例如AA 39" spellcheck="false" required></textarea></label>
    <div class="import-action-line"><button id="command-manual-preview-button" class="button" type="submit">生成并预览</button><button id="command-manual-cancel" class="button" type="button">取消</button><button id="command-manual-clear" class="button" type="button">清空</button></div></form>
    <p id="command-manual-error" class="form-error" role="alert"></p>
    <div id="command-manual-preview" class="hidden"><p id="command-manual-summary" class="import-summary"></p><div id="command-manual-issues"></div><div class="command-code-columns"><label>完整UDP源码<textarea id="command-manual-hex" class="mono" rows="5" readonly spellcheck="false"></textarea></label><label>数据内容区<textarea id="command-manual-data-hex" class="mono" rows="5" readonly spellcheck="false"></textarea></label></div><div class="import-action-line"><button id="command-manual-apply" class="button primary" disabled>确认入库</button><span class="muted small-text">自动计算长度和外层累加和；完整UDP源码完全相同时跳过。修改输入后须重新预览。</span></div></div>`;
  $('command-xml-panel').after(panel);
  const defaults={ZK:'192.168.1.111',BC:'192.168.1.113',KC:'192.168.1.137',PSS:'192.168.1.111'};
  let preview=null,busy=false,applying=false,generation=0,controller=null;
  function controls(){for(const input of $('command-manual-form').querySelectorAll('input,select,textarea'))input.disabled=applying;$('command-manual-preview-button').disabled=busy;$('command-manual-apply').disabled=busy||!preview?.canApply;$('command-manual-cancel').disabled=$('command-manual-clear').disabled=applying;}
  function cancel(notify=false){generation++;controller?.abort();controller=null;const token=preview?.token;preview=null;busy=applying=false;if(token)api('send-monitor/commands/preview/cancel',{token}).catch(()=>{});$('command-manual-preview').classList.add('hidden');$('command-manual-error').textContent='';$('command-manual-hex').value=$('command-manual-data-hex').value='';$('command-manual-issues').replaceChildren();controls();if(notify)toast('已取消手动指令预览，指令库未修改');}
  function show(data){preview=data;$('command-manual-preview').classList.remove('hidden');const row=data.xmlRows?.[0];$('command-manual-summary').textContent=row?[(row.code||'未分配编号'),row.name,row.destination,row.status,row.payloadLength?row.payloadLength+'字节':''].filter(Boolean).join(' · '):'未生成指令';$('command-manual-hex').value=row?.packetHex||'';$('command-manual-data-hex').value=row?.dataHex||'';$('command-manual-issues').replaceChildren(...(data.warnings||[]).map(text=>element('p','muted small-text',text)),...(row?.matches||[]).map(text=>element('p','muted small-text','已匹配：'+text)),...data.errors.map(text=>element('p','form-error',text)));controls();}
  function updateWidth(){const short=Number($('command-manual-part').value.trim())===0xAA;$('command-manual-interface-label').textContent='接口类型（gongnengid，'+(short?'2':'4')+'字节）';$('command-manual-interface').placeholder=short?'0～65535，支持0x十六进制':'0～4294967295，支持0x十六进制';}
  $('command-manual-form').addEventListener('input',()=>{if(!applying){cancel();updateWidth();}});
  $('command-manual-kind').addEventListener('change',()=>{cancel();const kind=$('command-manual-kind').value;$('command-manual-destination').value=defaults[kind];$('command-manual-name').placeholder='留空使用“手动'+kind+'指令”';const code=$('command-manual-code').value.trim(),digits=code.replace(/^[A-Za-z]+/,'');if(code&&/^\d+$/.test(digits))$('command-manual-code').value=kind+digits.replace(/^0+(?=\d)/,'').padStart(kind==='PSS'?3:4,'0');});
  $('command-manual-form').addEventListener('submit',async event=>{
    event.preventDefault();if(busy)return;window.SendMonitorUI?.discardImports();cancel();const turn=++generation;busy=true;controller=new AbortController();controls();
    try{const body={name:$('command-manual-name').value.trim(),kind:$('command-manual-kind').value,code:$('command-manual-code').value.trim(),destinationIP:$('command-manual-destination').value.trim(),bujianid:$('command-manual-part').value.trim(),gongnengid:$('command-manual-interface').value.trim(),datastr:$('command-manual-data').value};const response=await fetch('/api/send-monitor/commands/preview?format=stc-manual',{method:'POST',credentials:'same-origin',headers:{'Content-Type':'application/json','X-CSRF-Token':app.user.csrf},body:JSON.stringify(body),signal:controller.signal});const data=await response.json();if(turn!==generation)return;if(!response.ok)throw Error(data.error||'手动指令预览失败');show(data);}
    catch(error){if(turn===generation&&error.name!=='AbortError')$('command-manual-error').textContent=error.message;}
    finally{if(turn===generation){busy=false;controller=null;controls();}}
  });
  $('command-manual-apply').addEventListener('click',async()=>{if(busy||!preview?.canApply)return;const turn=generation;busy=applying=true;controls();try{await api('send-monitor/commands/apply',{token:preview.token});if(turn!==generation)return;preview=null;$('command-manual-preview').classList.add('hidden');await window.SendMonitorUI?.loadCatalog();toast('手动STC指令已入库');}catch(error){if(turn===generation)$('command-manual-error').textContent=error.message;}finally{if(turn===generation){busy=applying=false;controls();}}});
  $('command-manual-cancel').addEventListener('click',()=>cancel(true));$('command-manual-clear').addEventListener('click',()=>{cancel();$('command-manual-form').reset();updateWidth();});
  updateWidth();controls();return{cancel,reset(){cancel();$('command-manual-form').reset();updateWidth();}};
})();
