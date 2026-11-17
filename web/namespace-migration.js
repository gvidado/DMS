'use strict';
window.NamespaceMigrationUI=(()=>{
  const dialog=element('dialog','packet-dialog');dialog.id='namespace-migration-dialog';
  dialog.innerHTML=`<div class="dialog-heading"><div><h2>确认旧规则的数据来源</h2><p>选择TME、TMB或多个映射；原规则在确认保存前保持停用。</p></div><button id="migration-close" type="button" class="icon-button" aria-label="关闭迁移">×</button></div><div class="section-pad"><p id="migration-original"></p><details class="import-details"><summary>查看原规则</summary><pre id="migration-original-script" class="mono"></pre></details><h3>选择迁移目标</h3><div id="migration-targets"></div><p id="migration-empty" class="muted small-text hidden">尚无可选映射。请先在SPP配置填写外层数据源，并关联该参数的解码表。</p><div class="import-action-line"><button id="migration-preview" type="button" class="button">生成迁移预览</button></div><div id="migration-rows"></div><p id="migration-error" class="form-error" role="alert"></p><div class="import-action-line"><button id="migration-cancel" type="button" class="button">取消</button><button id="migration-apply" type="button" class="button primary" disabled>确认迁移并保持停用</button></div></div>`;
  document.body.append(dialog);let draft=null,choices=null,busy=false,epoch=0;
  function controls(){for(const id of ['migration-close','migration-cancel','migration-preview'])$(id).disabled=busy;$('migration-apply').disabled=busy||!draft;for(const input of $('migration-targets').querySelectorAll('input'))input.disabled=busy;}
  async function open(id){
    const version=++epoch;draft=choices=null;$('migration-error').textContent='';$('migration-targets').replaceChildren();$('migration-rows').replaceChildren();$('migration-original').textContent='正在读取旧规则…';$('migration-original-script').textContent='';$('migration-empty').classList.add('hidden');busy=true;controls();dialog.showModal();
    try{const data=await api('continuous-rules/migration?id='+encodeURIComponent(id));if(version!==epoch)return;choices=data;
      $('migration-original').textContent=data.rule.Code+' / '+data.rule.Name+' · '+data.rule.MigrationReason;$('migration-original-script').textContent=data.rule.Script;
      for(const target of data.targets){const label=element('label','checkbox-line'),check=element('input');check.type='checkbox';check.value=target.id;check.checked=data.targets.length===1;check.addEventListener('change',()=>{draft=null;$('migration-rows').replaceChildren();controls();});label.append(check,document.createTextNode(target.reference+' / '+target.packet+' / '+(target.sourceIP||'来源IP需明确')));$('migration-targets').append(label);}
      $('migration-empty').classList.toggle('hidden',data.targets.length>0);
    }catch(e){if(version===epoch)$('migration-error').textContent=e.message;}finally{if(version===epoch){busy=false;controls();}}
  }
  $('migration-preview').addEventListener('click',async()=>{
    if(busy||!choices)return;const targets=[...$('migration-targets').querySelectorAll('input:checked')].map(i=>i.value);if(!targets.length){$('migration-error').textContent='请先选择至少一个目标';return;}
    const version=epoch;busy=true;draft=null;controls();$('migration-error').textContent='';
    try{const data=await api('continuous-rules/migration-preview',{id:choices.rule.Id,targets});if(version!==epoch)return;draft=data;$('migration-rows').replaceChildren();
      for(const row of data.rows){const block=element('section','form-section');block.append(element('h3','',row.reference+' / '+row.packet));
        const sourceLabel=element('label','rule-script-label','来源IP（可留空按SPP绑定）'),source=element('input');source.value=row.rule.SourceIP||'';source.addEventListener('input',()=>row.rule.SourceIP=source.value.trim());sourceLabel.append(source);block.append(sourceLabel);
        const label=element('label','rule-script-label','判读规则（可修改后确认）'),script=element('textarea','migration-script');script.rows=9;script.maxLength=20000;script.spellcheck=false;script.value=row.rule.Script;script.addEventListener('input',()=>row.rule.Script=script.value);label.append(script);block.append(label);
        if(row.error)block.append(element('p','form-error','预览检查：'+row.error+'。可修正规则后确认；保存时会重新检查。'));
        block.append(element('p','muted small-text','迁移保留原等级、名称和规则内容；保存后保持停用，核对后再手动开启。'));$('migration-rows').append(block);
      }
    }catch(e){if(version===epoch)$('migration-error').textContent=e.message;}finally{if(version===epoch){busy=false;controls();}}
  });
  $('migration-apply').addEventListener('click',async()=>{
    if(busy||!draft)return;const version=epoch;busy=true;controls();$('migration-error').textContent='';
    try{await api('continuous-rules/migration-apply',{id:draft.id,revision:draft.revision,decoderRevision:draft.decoderRevision,rules:draft.rows.map(r=>r.rule)});if(version!==epoch)return;dialog.close();draft=null;await window.ContinuousUI.load();toast('规则已迁移并保持停用，请核对后开启');}
    catch(e){if(version===epoch)$('migration-error').textContent=e.message;}finally{if(version===epoch){busy=false;controls();}}
  });
  function cancel(){if(busy)return;epoch++;draft=choices=null;dialog.close();}
  ['migration-close','migration-cancel'].forEach(id=>$(id).addEventListener('click',cancel));
  dialog.addEventListener('cancel',event=>{if(busy)event.preventDefault();else cancel();});
  return {open,reset(){epoch++;draft=choices=null;busy=false;dialog.close();}};
})();
