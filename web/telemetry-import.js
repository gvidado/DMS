'use strict';
window.TelemetryImportUI = (() => {
  const fields = ['参数代号','参数名称','起始字节','字节个数','起始位','位置','数据类型','系数','单位','缺省显示类型','精度','物理意义'];
  const tbody = $('import-parameters'), details = tbody.closest('details'), header = tbody.closest('table').querySelector('thead tr');
  const actionHeader = element('th', '', '修正 / 舍弃'); header.append(actionHeader);
  const toolbar = element('div','ti-toolbar');
  toolbar.innerHTML = '<label>显示范围 <select id="ti-filter"><option value="all">全部参数</option><option value="error">有错误</option><option value="kept">待导入</option><option value="discarded">已舍弃</option></select></label><span id="ti-count" class="muted small-text" role="status"></span><button type="button" class="button small" id="ti-prev-page">上一页</button><span id="ti-page" class="small-text"></span><button type="button" class="button small" id="ti-next-page">下一页</button>';
  $('import-parameter-search').after(toolbar);
  const footnote = details.querySelector('p.muted'); const oldFootnote = footnote.textContent;
  const dialog = element('dialog','ti-dialog'); dialog.id = 'ti-dialog'; dialog.setAttribute('aria-labelledby','ti-title');
  dialog.innerHTML = `<form id="ti-form"><div class="ti-heading"><div><h2 id="ti-title">修正遥测参数</h2><p id="ti-origin" class="muted small-text"></p></div><button id="ti-close" type="button" class="button small">关闭</button></div><div class="ti-body"><div class="ti-context-grid"><aside class="ti-context" id="ti-previous"></aside><section class="ti-current"><h3>当前参数 · 可直接修改</h3><div id="ti-fields" class="ti-fields"></div><label class="ti-discard check-label"><input type="checkbox" id="ti-discard">舍弃本参数（本次不导入，可恢复）</label><div id="ti-row-issues" class="ti-row-issues"></div></section><aside class="ti-context" id="ti-next"></aside></div><section class="ti-suggestions"><h3>改正建议</h3><p class="muted small-text">建议按当前保存的预览内容计算。采纳只填入编辑框，保存并重新检查后生效；请核实地址、类型及系数，不会自动移动其他参数。</p><div id="ti-suggestions"></div></section></div><div class="ti-footer"><span id="ti-message" role="status" class="small-text"></span><button type="button" id="ti-previous-button" class="button small">上一个参数</button><button type="button" id="ti-next-button" class="button small">下一个参数</button><button type="submit" id="ti-save" class="button primary">保存修改并重新检查</button></div></form>`;
  document.body.append(dialog);
  const inputs = new Map();
  for (const name of fields) {
    const label = element('label', name === '物理意义' ? 'ti-wide' : '', name);
    const input = document.createElement(name === '物理意义' ? 'textarea' : 'input'); input.dataset.tiField = name; input.setAttribute('aria-label',name); input.maxLength = name === '物理意义' ? 100000 : 1000;
    if (name === '物理意义') input.rows = 3;
    if (['起始字节','字节个数','系数','精度'].includes(name)) input.inputMode = 'decimal';
    if (name === '数据类型') input.setAttribute('list','ti-types');
    if (name === '起始位') input.placeholder = '空白、0（整字节）或B0～B7';
    if (name === '系数') input.placeholder = '空白或/时按1处理';
    if (name === '精度') input.placeholder = '0～12；空白或/用原默认值';
    label.append(input); $('ti-fields').append(label); inputs.set(name,input);
  }
  const types = element('datalist'); types.id = 'ti-types'; for (const value of ['无符号整数','有符号整数','单精度浮点数','双精度浮点数','ASCII','BCD']) { const option = element('option'); option.value = value; types.append(option); } dialog.append(types);
  let current = null, token = '', epoch = 0, busy = false, dirty = false, page = 0, filterQuery = '', currentData = null;
  function button(text, action) { const b = element('button','button small',text); b.type = 'button'; b.addEventListener('click',action); return b; }
  function cell(row, text, cls='') { const td = element('td',cls,text); row.append(td); return td; }
  function hide() { toolbar.classList.add('hidden'); actionHeader.classList.add('hidden'); footnote.textContent = oldFootnote; }
  function render(preview) {
    toolbar.classList.remove('hidden'); actionHeader.classList.remove('hidden');
    const review = preview.review, query = $('import-parameter-search').value.trim().toLowerCase(), filter = $('ti-filter').value;
    const signature = query+'|'+filter+'|'+preview.token; if (signature !== filterQuery) { page=0; filterQuery=signature; }
    const parameters = new Map(preview.configuration.Catalog.Parameters.map(p=>[p.Id,p]));
    const rows = review.rows.filter(r=>(filter!=='error'||(!r.discarded&&r.errors>0))&&(filter!=='discarded'||r.discarded)&&(filter!=='kept'||!r.discarded)&&[r.code,r.name,parameterValueReference({Code:r.code}),r.sheet,r.row,r.position,parameters.get(r.parameterId)?.Note].join(' ').toLowerCase().includes(query));
    const pages = Math.max(1,Math.ceil(rows.length/100)); page = Math.max(0,Math.min(page,pages-1)); const fragment = document.createDocumentFragment();
    for (const r of rows.slice(page*100,(page+1)*100)) {
      const tr = element('tr',r.discarded?'ti-discarded-row':r.errors?'ti-error-row':''); tr.dataset.importKey = r.key;
      const first = cell(tr,(r.name||'（名称缺失）')+' / '+(r.code||'（代号缺失）')); first.append(element('div','muted small-text',r.sheet+' · Excel第'+r.row+'行'));
      cell(tr,(r.position||'第'+(r.start||'？')+'字节')+' / '+(r.type||'类型缺失'));
      cell(tr,'× '+(!r.coefficient||['/','／'].includes(r.coefficient)?'1（默认）':r.coefficient),'mono'); cell(tr,r.discarded?'已舍弃':r.errors?r.errors+'项错误：'+r.issues.filter(i=>i.Level==='error').map(i=>i.Message).join('；'):'可导入');
      const actions = cell(tr,''); actions.append(button('编辑 / 查看上下文',()=>open(r.key))); fragment.append(tr);
    }
    if (!rows.length) { const tr = element('tr'), td=cell(tr,'没有符合条件的参数'); td.colSpan=5; fragment.append(tr); }
    tbody.replaceChildren(fragment);
    $('ti-count').textContent = '共'+review.total+'项 · 待导入'+(review.total-review.discarded)+'项 · 舍弃'+review.discarded+'项 · 错误'+review.errors+'项 · 匹配'+rows.length+'项';
    $('ti-page').textContent = (page+1)+' / '+pages; $('ti-prev-page').disabled=page===0; $('ti-next-page').disabled=page+1>=pages;
    footnote.textContent='可分页查看全部参数；点击“编辑 / 查看上下文”修改错误、查看建议或选择舍弃。保存预览后，仍需选择添加或替换才会更新当前配置。';
    if (review.errors) details.open=true;
  }
  function renderCurrent() { const preview=DecoderUI.importPreview(); if (preview?.review) render(preview); }
  $('ti-filter').addEventListener('change',renderCurrent); $('ti-prev-page').addEventListener('click',()=>{page--;renderCurrent();}); $('ti-next-page').addEventListener('click',()=>{page++;renderCurrent();});
  function context(id,row,title) {
    const target=$(id); target.replaceChildren(element('h3','',title));
    if (!row) { target.append(element('p','muted small-text','本工作表中没有'+title+'，不会跨表推断。')); return; }
    target.append(element('p','ti-context-origin',row.sheet+' · Excel第'+row.row+'行'+(row.discarded?' · 已舍弃':'')));
    const dl=element('dl'); for (const name of fields) { dl.append(element('dt','',name),element('dd','',row.values[name]||'（空白）')); } target.append(dl);
    if(row.issues.length) target.append(element('p','issue-warning small-text',row.issues.map(i=>i.Message).join('；')));
  }
  function lock(value) { busy=value; DecoderUI.importReviewBusy(value); dialog.querySelectorAll('button,input,textarea').forEach(el=>el.disabled=value); if(!value) { $('ti-save').disabled=!currentData; $('ti-previous-button').disabled=!currentData?.previous; $('ti-next-button').disabled=!currentData?.next; } }
  function close(force=false) { if(busy&&!force)return; if(!force&&dirty&&!confirm('尚有未保存的参数修改，确定放弃这些修改并关闭？'))return; ++epoch; current=null; currentData=null; dirty=false; if(dialog.open)dialog.close(); }
  async function open(key,skipDirty=false) {
    if(busy)return; if(!skipDirty&&dirty&&!confirm('当前参数修改尚未保存，确定放弃并切换？'))return;
    const preview=DecoderUI.importPreview(); if(!preview?.review)return; const request=++epoch; token=preview.token; current=key; currentData=null; lock(true);
    $('ti-row-issues').replaceChildren(); $('ti-suggestions').replaceChildren();
    if(!dialog.open)dialog.showModal(); $('ti-message').textContent='正在读取参数及上下文…';
    try {
      const data=await api('decoders/preview/parameter?token='+encodeURIComponent(token)+'&key='+encodeURIComponent(key));
      if(request!==epoch||DecoderUI.importPreview()?.token!==data.token)return;
      currentData=data; dirty=false; $('ti-title').textContent='修正遥测参数 · '+(data.current.values['参数代号']||'代号待填写'); $('ti-origin').textContent=data.current.sheet+' · Excel第'+data.current.row+'行 · 仅修改本次导入预览，原Excel文件不变';
      for(const name of fields)inputs.get(name).value=data.current.values[name]||'';
      $('ti-discard').checked=data.current.discarded; context('ti-previous',data.previous,'上一参数'); context('ti-next',data.next,'下一参数');
      $('ti-row-issues').replaceChildren(...data.current.issues.map(i=>element('p',i.Level==='error'?'issue-error small-text':'issue-warning small-text',i.Message)));
      if(!data.current.issues.length)$('ti-row-issues').append(element('p','muted small-text','本参数定义检查通过。'));
      const suggestions=$('ti-suggestions'); suggestions.replaceChildren();
      for(const suggestion of data.suggestions) {
        const card=element('div','ti-suggestion'),text=element('div');text.append(element('strong','',suggestion.Title),element('p','muted small-text',suggestion.Reason),element('p','mono small-text',Object.entries(suggestion.Values).map(([k,v])=>k+' = '+(v||'（空白）')).join('；')));
        card.append(text,button('采纳建议',()=>{for(const [k,v]of Object.entries(suggestion.Values))inputs.get(k).value=v;dirty=true;$('ti-message').textContent='建议已填入，请核对后保存并重新检查。';}));suggestions.append(card);
      }
      if(!data.suggestions.length)suggestions.append(element('p','muted small-text','当前上下文没有可可靠推导的修正建议。请对照遥测定义手工修改，或选择舍弃本参数；名称、系数等不能仅凭相邻项确定。'));
      $('ti-message').textContent=data.current.discarded?'该参数已舍弃，取消勾选后保存可恢复。':'修改后点击“保存修改并重新检查”。';
    }catch(e){if(request===epoch)$('ti-message').textContent=e.message;}finally{if(request===epoch)lock(false);}
  }
  $('ti-form').addEventListener('input',()=>{dirty=true;}); $('ti-close').addEventListener('click',()=>close());dialog.addEventListener('cancel',e=>{e.preventDefault();close();});
  $('ti-previous-button').addEventListener('click',()=>currentData?.previous&&open(currentData.previous.key));$('ti-next-button').addEventListener('click',()=>currentData?.next&&open(currentData.next.key));
  $('ti-form').addEventListener('submit',async event=>{
    event.preventDefault();if(busy||!currentData||DecoderUI.importPreview()?.token!==token)return;
    const request=epoch,key=current;lock(true);$('ti-message').textContent='正在保存预览并重新检查…';
    try {
      const data=await api('decoders/preview/parameter',{token,key,editRevision:currentData.editRevision,discarded:$('ti-discard').checked,values:Object.fromEntries(fields.map(name=>[name,inputs.get(name).value]))});
      if(request!==epoch||!DecoderUI.acceptImportReview(data))return;
      dirty=false;lock(false);await open(key,true);
      if(current===key&&DecoderUI.importPreview()?.token===data.token)$('ti-message').textContent='预览已保存并重新检查。'+(data.review.errors?'仍有'+data.review.errors+'项错误需修正或舍弃。':'全部保留参数检查通过，可关闭此框后选择导入方式。');
    }catch(e){if(request===epoch)$('ti-message').textContent=e.message;}finally{if(request===epoch)lock(false);}
  });
  hide();
  return { render, open, hide, reset() { close(true); lock(false); page=0;filterQuery='';$('ti-filter').value='all';hide(); } };
})();
