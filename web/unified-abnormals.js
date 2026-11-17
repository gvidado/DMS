'use strict';
window.UnifiedAbnormalUI = (() => {
  const panel=element('section','panel unified-abnormal-panel');panel.id='judgment-abnormal-panel';
  panel.innerHTML=`<div class="panel-heading"><div><h2>异常判读记录 <span id="abnormal-count" class="count-chip">0</span></h2><p>统一查看常态化判读、序列执行、序列判读及SPP接收异常；所有异常均须填写原因后解除。</p></div><a id="abnormal-export" class="button" href="/api/abnormal-records/export.csv" download="abnormal-records.csv">导出全部记录CSV</a></div><div class="section-pad"><div id="abnormal-summary" class="import-summary"></div><div class="rule-toolbar"><select id="abnormal-type" aria-label="异常类型筛选"><option value="all">全部类型</option><option value="continuous">常态化判读</option><option value="sequence-execute">序列执行</option><option value="sequence-judge">序列判读</option><option value="spp">SPP接收</option><option value="system">系统运行</option></select><select id="abnormal-level" aria-label="告警或异常筛选"><option value="all">全部告警与异常</option><option value="warn">仅告警</option><option value="error">仅异常</option></select><select id="abnormal-status" aria-label="异常解除状态筛选"><option value="pending">未解除</option><option value="released">已解除</option><option value="all">全部异常</option></select><input id="abnormal-search" placeholder="搜索参数、序列、SPP包、来源或异常信息" aria-label="搜索异常记录"><button id="abnormal-refresh" class="button">刷新最近记录</button></div><p class="muted small-text">解除只处理本条历史记录，保留异常发生时的数据和规则，不改变实时参数或运行结论。旧版本的确认信息继续保留；尚无解除原因的记录仍需补填原因。CSV包含全部类型与解除信息。</p><p id="abnormal-error" class="form-error" role="alert"></p><div class="table-scroll"><table class="data-table"><thead><tr><th>记录号 / 发生时间</th><th>级别 / 类型</th><th>参数 / 序列 / SPP包</th><th>异常值 / 异常说明</th><th>解除状态</th><th>解除原因</th><th>规则 / 操作</th></tr></thead><tbody id="abnormal-rows"></tbody></table></div><div class="current-table-pagination"><span id="abnormal-page-note"></span><div><button id="abnormal-previous" class="button small">上一页</button> <button id="abnormal-next" class="button small">下一页</button></div></div></div>`;
  $('view-results').append(panel);
  const releaseAll=element('button','button','一键解除全部未解除异常');releaseAll.id='abnormal-release-all';
  panel.querySelector('.rule-toolbar').append(releaseAll);

  const dialog=element('dialog','packet-dialog');dialog.id='abnormal-dialog';
  dialog.innerHTML=`<div class="dialog-heading"><h2>填写原因并解除异常</h2><button id="abnormal-close" class="icon-button" aria-label="关闭异常解除">×</button></div><form id="abnormal-release-form" class="section-pad"><p id="abnormal-release-target"></p><p id="abnormal-release-value" class="mono small-text"></p><label class="rule-script-label">解除原因（必填）<textarea id="abnormal-reason" rows="4" maxlength="2000" required placeholder="请填写异常分析或本次解除的原因"></textarea></label><p class="muted small-text">仅解除这条记录。原异常、运行结论、历史确认信息与解除原因均保留。</p><p id="abnormal-release-error" class="form-error" role="alert"></p><div class="import-action-line"><button id="abnormal-cancel" type="button" class="button">取消</button><button id="abnormal-release-save" type="submit" class="button primary">填写原因并解除</button></div></form>`;
  document.body.append(dialog);
  let epoch=0,request=0,page=1,judgmentThrough=0,alarmThrough=0,sequenceThrough=0,known='',signature='',data=null,polling=false,releasing=false,editing=null,searchTimer;
  const expandedRules=new Set();
  const labels={Normal:'正常',Unusual:'异常提醒',Error:'故障',SeriousError:'严重故障'};
  function td(row,text,cls=''){const cell=element('td',cls,text??'');row.append(cell);return cell;}
  function controls(){
    $('abnormal-refresh').disabled=releasing;
    releaseAll.disabled=releasing||!data||data.pending===0;
    for(const id of ['abnormal-type','abnormal-level','abnormal-status','abnormal-search','abnormal-release-save','abnormal-close','abnormal-cancel'])$(id).disabled=releasing;
    $('abnormal-previous').disabled=releasing || page<=1;$('abnormal-next').disabled=releasing || !data || page>=data.pages;
    panel.querySelectorAll('.abnormal-release').forEach(button=>button.disabled=releasing);
  }
  function render(){
    if(!data)return;const rows=document.createDocumentFragment();
    // Save the current user choice before replacing rows on a history update.
    for(const tr of $('abnormal-rows').querySelectorAll('tr[data-abnormal-key]')){
      const details=tr.querySelector('details');
      if(details?.open)expandedRules.add(tr.dataset.abnormalKey);else expandedRules.delete(tr.dataset.abnormalKey);
    }
    for(const row of data.records){
      const tr=element('tr');tr.dataset.abnormalKey=row.key;tr.dataset.abnormalId=row.id;tr.dataset.released=String(row.released);tr.dataset.level=row.level;
      const time=td(tr,row.key,'mono small-text');time.append(element('div','param-time',row.time));
      const type=td(tr,'');type.append(element('span','status-tag '+(row.level==='warn'?'warn':'bad'),row.level==='warn'?'告警':'异常'),element('div','small-text',row.typeName),element('div','muted small-text',row.category));
      const target=td(tr,row.code,'mono');target.append(element('div','param-name',row.name),element('div','muted small-text',[row.source,row.packet,row.mode].filter(Boolean).join(' / ')));
      const value=td(tr,row.value || '—','mono small-text');value.append(element('div','muted small-text',labels[row.result] || row.result));
      if(row.detail)value.append(element('div','small-text',row.detail));
      const state=td(tr,'');state.append(element('span','status-tag '+(row.released?'neutral':'bad'),row.released?'已解除':'未解除'));
      if(row.released)state.append(element('div','muted small-text',row.releasedBy+' / '+row.releasedAt));
      if(row.legacyConfirmed)state.append(element('div','muted small-text','原告警已确认：'+row.legacyConfirmedBy+' / '+row.legacyConfirmedAt));
      td(tr,row.reason || '—').style.whiteSpace='pre-wrap';
      const action=td(tr,''),details=element('details','import-details');details.append(element('summary','','发生时规则 / 诊断'));
      details.open=expandedRules.has(row.key);
      const rule=element('pre','mono small-text',row.rule || row.detail || '—');rule.style.whiteSpace='pre-wrap';rule.style.overflowWrap='anywhere';details.append(rule);action.append(details);
      if(!row.released){const button=element('button','button small abnormal-release','填写原因并解除');button.addEventListener('click',()=>openRelease(row));action.append(button);}rows.append(tr);
    }
    if(!data.records.length){const tr=element('tr');td(tr,'当前没有符合条件的异常记录。','muted').colSpan=7;rows.append(tr);}
    $('abnormal-rows').replaceChildren(rows);setText('abnormal-count',data.total);
    $('abnormal-summary').replaceChildren(element('strong','',data.total+'条历史'),element('span','',data.pending+'条未解除'),element('span','',data.released+'条已解除'));
    setText('abnormal-page-note','第'+data.page+'/'+data.pages+'页 · 筛选共'+data.matching+'条');setText('abnormal-error',data.storageError || '');controls();
  }
  async function load(force=false){
    if(!app.user || releasing || (polling && !force))return;
    const version=epoch,serial=++request,requestedPage=page,type=$('abnormal-type').value,level=$('abnormal-level').value,status=$('abnormal-status').value,search=$('abnormal-search').value.trim();
    const key=JSON.stringify([requestedPage,type,level,status,search,judgmentThrough,alarmThrough,sequenceThrough]);polling=true;
    const query=new URLSearchParams({page:String(page),pageSize:'50',type,level,status,search,judgmentThrough:page===1?'0':String(judgmentThrough),alarmThrough:page===1?'0':String(alarmThrough),sequenceThrough:page===1?'0':String(sequenceThrough),knownRevision:!force && key===signature?known:''});
    try{
      const result=await api('abnormal-records?'+query);if(version!==epoch || serial!==request)return;
      if(result.unchanged)setText('abnormal-error',data?.storageError || '');
      if(!result.unchanged){data=result;page=result.page;judgmentThrough=result.judgmentThrough;alarmThrough=result.alarmThrough;sequenceThrough=result.sequenceThrough||0;known=result.revision;signature=JSON.stringify([page,type,level,status,search,judgmentThrough,alarmThrough,sequenceThrough]);app.unifiedAbnormalSummary={total:result.total,pending:result.pending,released:result.released};render();}
    }catch(e){if(version===epoch && serial===request)setText('abnormal-error',e.message);}
    finally{if(version===epoch && serial===request){polling=false;controls();}}
  }
  function latest(){page=1;judgmentThrough=alarmThrough=sequenceThrough=0;known='';load(true);}
  function openRelease(row){
    if(releasing||dialog.open)return;editing=row;setText('abnormal-release-target',row.key+' / '+row.typeName+' / '+row.code+' / '+row.name);
    setText('abnormal-release-value',(row.value?'异常值：'+row.value+'；':'')+(row.detail || labels[row.result] || row.result));
    dialog.querySelector('h2').textContent='填写原因并解除异常';
    dialog.querySelector('p.muted').textContent='仅解除这条记录。原异常、运行结论、历史确认信息与解除原因均保留。';
    $('abnormal-reason').value='';setText('abnormal-release-error','');dialog.showModal();
  }
  releaseAll.addEventListener('click',()=>{
    if(releasing||dialog.open||!data?.pending)return;editing={all:true};
    dialog.querySelector('h2').textContent='填写共同原因并解除全部异常';
    setText('abnormal-release-target','全部类型、全部分页的未解除异常（当前 '+data.pending+' 条）');
    setText('abnormal-release-value','范围不受当前筛选限制；提交时已存在的未解除记录全部处理，之后新发生的异常仍需另行解除。');
    dialog.querySelector('p.muted').textContent='每条异常分别保存共同原因、解除时间和操作人；原异常和运行结论保留，已解除记录的原原因不覆盖。';
    $('abnormal-reason').value='';setText('abnormal-release-error','');dialog.showModal();
  });
  $('abnormal-release-form').addEventListener('submit',async event=>{
    event.preventDefault();if(releasing || !editing)return;const reason=$('abnormal-reason').value.trim();
    if(!reason){setText('abnormal-release-error','请填写解除原因');return;}
    releasing=true;controls();const version=epoch;
    try{
      const all=editing.all,result=await api(all?'abnormal-records/release-all':'abnormal-records/release',all?{reason}:{key:editing.key,reason});
      if(version!==epoch)return;
      if(all&&!result.ok)throw Error('已解除 '+result.released+' 条，剩余 '+result.remaining+' 条。'+result.error);
      dialog.close();window.dispatchEvent(new Event('abnormal-records-changed'));
      toast(all?'已解除 '+result.released+' 条异常，共同原因已逐条保存':'异常已填写原因并解除，原始记录已保留');
    }
    catch(e){if(version===epoch){setText('abnormal-release-error',e.message);return;}}
    finally{if(version===epoch){releasing=false;controls();if(!dialog.open){latest();const current=await api('state').catch(()=>null);if(current && version===epoch){app.state=current;renderState();}}}}
  });
  ['abnormal-close','abnormal-cancel'].forEach(id=>$(id).addEventListener('click',()=>dialog.close()));dialog.addEventListener('cancel',event=>{if(releasing)event.preventDefault();});
  for(const id of ['abnormal-type','abnormal-level','abnormal-status'])$(id).addEventListener('change',latest);
  $('abnormal-search').addEventListener('input',()=>{clearTimeout(searchTimer);searchTimer=setTimeout(latest,250);});
  $('abnormal-refresh').addEventListener('click',latest);
  $('abnormal-previous').addEventListener('click',()=>{if(page>1){page--;known='';load(true);}});
  $('abnormal-next').addEventListener('click',()=>{if(data && page<data.pages){page++;known='';load(true);}});
  function open(){if(app.view!=='results'){window.WorkspaceUI.open('results','judgment-abnormal-panel');return;}navigate('results');latest();panel.scrollIntoView({block:'start',behavior:'smooth'});}
  function reset(){++epoch;++request;polling=releasing=false;data=editing=null;page=1;judgmentThrough=alarmThrough=sequenceThrough=0;known=signature='';expandedRules.clear();app.unifiedAbnormalSummary=null;clearTimeout(searchTimer);dialog.close();$('abnormal-rows').replaceChildren();$('abnormal-summary').replaceChildren();$('abnormal-search').value='';$('abnormal-type').value='all';$('abnormal-level').value='all';$('abnormal-status').value='pending';setText('abnormal-error','');setText('abnormal-page-note','');setText('abnormal-count','0');controls();}
  setInterval(()=>{if(app.user&&['parameters','results','overview'].includes(app.view))load();},1500);controls();return {load,open,openRelease,reset};
})();
