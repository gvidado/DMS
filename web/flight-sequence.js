'use strict';
window.FlightSequenceUI=(()=>{
  const open=element('button','button','飞行判读表转换');
  open.id='flight-sequence-open';
  $('sequence-convert-export').after(open);
  const dialog=element('dialog','packet-dialog sequence-flight-dialog');
  dialog.setAttribute('aria-labelledby','flight-sequence-title');
  dialog.innerHTML=`
    <div class="dialog-heading">
      <h2 id="flight-sequence-title">飞行判读表转换</h2>
      <button id="flight-sequence-close" class="icon-button" aria-label="关闭飞行判读表转换">×</button>
    </div>
    <div class="section-pad sequence-flight-body">
      <p class="muted small-text">读取飞行阶段、飞行动作、参数代号、参考值、范围值和关键字。范围值填 0 表示等于参考值；填写大于或小于条件时可留空。</p>
      <div class="rule-toolbar">
        <input id="flight-sequence-file" type="file" accept=".xlsx" aria-label="飞行判读Excel文件">
        <button id="flight-sequence-read" class="button" disabled>读取并预览</button>
        <button id="flight-sequence-clear" class="button">清空</button>
      </div>
      <p id="flight-sequence-error" class="form-error" role="alert" hidden></p>
      <section id="flight-sequence-preview" hidden>
        <div class="rule-toolbar sequence-flight-tools">
          <label>工作表 <select id="flight-sequence-sheet"></select></label>
          <span id="flight-sequence-status" aria-live="polite"></span>
          <div class="rule-toolbar sequence-flight-pages">
            <button id="flight-sequence-prev" class="button small">上一页</button>
            <span id="flight-sequence-page"></span>
            <button id="flight-sequence-next" class="button small">下一页</button>
          </div>
        </div>
        <details id="flight-sequence-checks" hidden>
          <summary>检查结果</summary>
          <ul id="flight-sequence-messages"></ul>
        </details>
        <div class="table-scroll sequence-flight-scroll">
          <table class="data-table sequence-flight-table">
            <thead><tr><th>原表行号</th><th>飞行阶段</th><th>飞行动作</th><th>关键字</th><th>参数代号</th><th>参考值</th><th>范围值</th><th>转换结果</th></tr></thead>
            <tbody id="flight-sequence-rows"></tbody>
          </table>
        </div>
        <datalist id="flight-sequence-parameters"></datalist>
      </section>
    </div>
    <div class="dialog-footer">
      <button id="flight-sequence-cancel" class="button">取消</button>
      <button id="flight-sequence-recheck" class="button" disabled>重新检测当前工作表</button>
      <button id="flight-sequence-export" class="button primary" disabled>导出序列判读 Excel</button>
    </div>`;
  document.body.append(dialog);

  const file=$('flight-sequence-file');
  const select=$('flight-sequence-sheet');
  const pending=new Map();
  const pages=new Map();
  const pageSize=50;
  let token='',sheets=[],epoch=0,busy=false;
  const current=()=>sheets.find(item=>item.Id===select.value);
  const csrf=()=>app.user?.csrf||'';
  const jsonOptions=(body,key=csrf())=>({
    method:'POST',headers:{'Content-Type':'application/json','X-CSRF-Token':key},body:JSON.stringify(body)
  });

  function cancel(value,key=csrf()) {
    if(value&&key) fetch('/api/sequence/flight/cancel',jsonOptions({token:value},key)).catch(()=>{});
  }

  function error(message) {
    const field=$('flight-sequence-error');
    field.hidden=!message;
    field.textContent=message;
  }

  function clear(resetFile=true) {
    ++epoch;
    cancel(token);
    token='';
    sheets=[];
    pending.clear();
    pages.clear();
    busy=false;
    if(resetFile) file.value='';
    select.replaceChildren();
    $('flight-sequence-rows').replaceChildren();
    error('');
    draw();
  }

  function controls() {
    const sheet=current();
    const dirty=Boolean(sheet&&pending.has(sheet.Id));
    const badSheets=sheets.filter(item=>item.Errors.length).length;
    const preview=$('flight-sequence-preview');
    preview.hidden=!sheet;
    preview.inert=busy;
    $('flight-sequence-read').disabled=busy||!file.files.length;
    $('flight-sequence-recheck').disabled=busy||!sheet;
    $('flight-sequence-export').disabled=busy||!token||pending.size>0||badSheets>0;
    select.disabled=busy;
    const status=$('flight-sequence-status');
    status.textContent=sheet
      ? [sheet.Rows.length+' 行',sheet.Errors.length+' 项错误',dirty?'有修改待重新检测':'',
        pending.size>Number(dirty)?'其他工作表有未检测修改':'',badSheets>Number(Boolean(sheet.Errors.length))?'其他工作表有错误':''].filter(Boolean).join(' · ')
      : '';
  }

  function candidates(input,row) {
    const query=input.value.trim().toLowerCase().replace(/[。．]/g,'.');
    const options=new Map((row.Candidates||[]).map(ref=>[ref,'']));
    for(const p of app.catalog) {
      if(options.size>=40) break;
      const ref=parameterReference(p);
      const name=p.Name||'';
      if(p.Supported===false||!ref) continue;
      if((ref+' '+name).toLowerCase().includes(query)||options.has(ref)) options.set(ref,name);
    }
    $('flight-sequence-parameters').replaceChildren(...Array.from(options,([value,label])=>new Option(label||value,value)));
  }

  function editCell(tr,sheet,row,key,label) {
    const td=tr.insertCell();
    const input=element('input','sequence-flight-input');
    input.type='text';
    input.autocomplete='off';
    input.maxLength=128;
    input.setAttribute('aria-label','第'+row.Input.Row+'行'+label);
    input.value=pending.get(sheet.Id)?.get(row.Input.Row)?.[key]??row.Input[key];
    if(row.Errors[label]) {
      input.setAttribute('aria-invalid','true');
      input.title=row.Errors[label];
    }
    if(key==='Parameter') {
      input.setAttribute('list','flight-sequence-parameters');
      input.addEventListener('focus',()=>candidates(input,row));
    }
    input.addEventListener('input',()=>{
      let changes=pending.get(sheet.Id);
      if(!changes) pending.set(sheet.Id,changes=new Map());
      const edited={...(changes.get(row.Input.Row)||row.Input),[key]:input.value};
      if(['Keyword','Parameter','Reference','Range'].every(field=>edited[field]===row.Input[field])) changes.delete(row.Input.Row);
      else changes.set(row.Input.Row,edited);
      if(!changes.size) pending.delete(sheet.Id);
      tr.classList.toggle('flight-sequence-edited',changes.has(row.Input.Row));
      if(key==='Parameter') candidates(input,row);
      controls();
    });
    td.append(input);
  }

  function draw() {
    controls();
    const sheet=current();
    if(!sheet) return;
    const last=Math.max(1,Math.ceil(sheet.Rows.length/pageSize));
    const page=Math.max(1,Math.min(last,pages.get(sheet.Id)||1));
    setText('flight-sequence-page',page+' / '+last);
    $('flight-sequence-prev').disabled=page===1;
    $('flight-sequence-next').disabled=page===last;
    const messages=[...sheet.Errors,...sheet.Warnings];
    const checks=$('flight-sequence-checks');
    checks.hidden=!messages.length;
    checks.open=false;
    checks.querySelector('summary').textContent='检查结果（'+sheet.Errors.length+' 项错误，'+sheet.Warnings.length+' 项提示）';
    $('flight-sequence-messages').replaceChildren(...messages.map(message=>element('li','',message)));

    const fragment=document.createDocumentFragment();
    for(const row of sheet.Rows.slice((page-1)*pageSize,page*pageSize)) {
      const tr=element('tr',pending.get(sheet.Id)?.has(row.Input.Row)?'flight-sequence-edited':'');
      for(const value of [row.Input.Row,row.Input.Phase,row.Input.Action]) {
        tr.insertCell().textContent=value;
      }
      for(const [key,label] of [['Keyword','关键字'],['Parameter','参数代号'],['Reference','参考值'],['Range','范围值']]) {
        editCell(tr,sheet,row,key,label);
      }
      const result=tr.insertCell();
      result.append(element('div','',row.Description));
      if(row.Condition) result.append(element('code','sequence-flight-condition',row.Condition));
      for(const message of Object.values(row.Errors)) result.append(element('div','sequence-flight-row-error',message));
      fragment.append(tr);
    }
    $('flight-sequence-rows').replaceChildren(fragment);
    dialog.querySelector('.sequence-flight-scroll').scrollTop=0;
  }

  async function checkResponse(response) {
    if(response.ok) return;
    if(response.status===401&&app.user) signedOut('登录已过期，请重新登录');
    const data=await response.json().catch(()=>({}));
    throw new Error(data.error||'请求失败（'+response.status+'）');
  }

  async function read() {
    const source=file.files[0];
    if(!source||busy) return;
    clear(false);
    const request=epoch,key=csrf();
    busy=true;
    controls();
    try {
      const response=await fetch('/api/sequence/flight/preview?filename='+encodeURIComponent(source.name),{
        method:'POST',headers:{'Content-Type':'application/octet-stream','X-CSRF-Token':key},body:source
      });
      await checkResponse(response);
      const preview=await response.json();
      if(request!==epoch) { cancel(preview.token,key); return; }
      token=preview.token;
      sheets=preview.sheets||[];
      select.replaceChildren(...sheets.map(sheet=>new Option(sheet.Name,sheet.Id)));
      draw();
    } catch(failure) {
      if(request===epoch) error('读取飞行判读表失败。'+failure.message);
    } finally {
      if(request===epoch) { busy=false;controls(); }
    }
  }

  async function recheck() {
    const sheet=current();
    if(!token||!sheet||busy) return;
    const request=epoch;
    busy=true;
    error('');
    controls();
    try {
      const edits=Array.from((pending.get(sheet.Id)||new Map()).values());
      const result=await api('sequence/flight/recheck',{token,sheetId:sheet.Id,edits});
      if(request!==epoch) return;
      sheets[sheets.indexOf(sheet)]=result.sheet;
      pending.delete(sheet.Id);
      draw();
    } catch(failure) {
      if(request===epoch) error('重新检测失败。'+failure.message);
    } finally {
      if(request===epoch) { busy=false;controls(); }
    }
  }

  async function exportXlsx() {
    if($('flight-sequence-export').disabled) return;
    const request=epoch;
    busy=true;
    error('');
    controls();
    try {
      const response=await fetch('/api/sequence/flight/export',jsonOptions({token}));
      await checkResponse(response);
      const blob=await response.blob();
      if(request!==epoch) return;
      const name=file.files[0].name.replace(/\.xlsx$/i,'')+'_序列判读.xlsx';
      const url=URL.createObjectURL(blob);
      const link=element('a');
      link.href=url;
      link.download=name;
      link.click();
      setTimeout(()=>URL.revokeObjectURL(url),30000);
    } catch(failure) {
      if(request===epoch) error('导出失败。'+failure.message);
    } finally {
      if(request===epoch) { busy=false;controls(); }
    }
  }

  function reset() {
    clear();
    if(dialog.open) dialog.close();
  }
  open.addEventListener('click',()=>dialog.showModal());
  dialog.addEventListener('close',()=>{if(!dialog.open) reset();});
  $('flight-sequence-close').addEventListener('click',reset);
  $('flight-sequence-cancel').addEventListener('click',reset);
  $('flight-sequence-clear').addEventListener('click',()=>clear());
  file.addEventListener('change',()=>clear(false));
  select.addEventListener('change',draw);
  for(const [dir,step] of [['prev',-1],['next',1]]) {
    $('flight-sequence-'+dir).addEventListener('click',()=>{
      const sheet=current();
      if(!sheet) return;
      pages.set(sheet.Id,(pages.get(sheet.Id)||1)+step);
      draw();
    });
  }
  $('flight-sequence-read').addEventListener('click',read);
  $('flight-sequence-recheck').addEventListener('click',recheck);
  $('flight-sequence-export').addEventListener('click',exportXlsx);
  return {reset};
})();
