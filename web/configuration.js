'use strict';
window.ConfigurationUI=(()=>{
  const note=element('div','configuration-schedule hidden');note.id='configuration-schedule';note.setAttribute('role','status');
  const text=element('span'),cancel=element('button','button small','取消待生效修改');cancel.type='button';note.append(text,cancel);document.querySelector('.topbar').after(note);
  const labels={'server.json':'监听','parameters.json':'遥测参数','decoder.json':'参数/SPP包','continuous-rules.json':'常态化判读','tc-bindings.json':'TC绑定','sequences.json':'序列','observed-commands.json':'指令库','send-monitor.json':'发送监视','command-send.json':'发送端口'};
  let polling=false,last='',user=null,next=0;
  function readPath(path,body){
    if(body!==undefined||!['settings','decoders','judgments','sequence-config','command-config'].includes(app.view)||new URLSearchParams(path.split('?')[1]||'').has('draft'))return path;
    const bare=path.split('?')[0];
    const eligible=(bare.startsWith('decoders')&&app.view==='decoders')||
      (bare.startsWith('continuous-rules')&&app.view==='judgments'&&!/\/(globals|abnormals|control)/.test(bare))||
      (app.view==='sequence-config'&&['sequence/config','sequence/definition'].includes(bare))||
      (app.view==='command-config'&&(bare==='send-monitor/state'||bare.startsWith('send-monitor/commands')||bare==='command-send/config'));
    return eligible?path+(path.includes('?')?'&':'?')+'draft=1':path;
  }
  async function poll(){
    if(!app.user){note.classList.add('hidden');user=null;last='';return;}if(polling)return;polling=true;const owner=app.user;
    try{
      const state=await api('configuration/schedule');if(app.user!==owner)return;
      if(user!==owner){user=owner;last=state.applyId||state.lastAppliedAt||'';}
      note.classList.toggle('hidden',!state.pending&&!state.error);note.classList.toggle('configuration-failed',!!state.error);cancel.classList.toggle('hidden',!state.pending&&!state.error);
      text.textContent=state.error||('待生效：'+state.files.map(f=>labels[f]||f).join('、')+(state.monitoring?' · 距下次统一切换 '+state.nextInSeconds+' 秒（每2分钟一次） · 接收与判读继续运行':' · 监听已停止，立即应用修改'));
      if(state.lastAppliedAt&&(state.applyId||state.lastAppliedAt)!==last){last=state.applyId||state.lastAppliedAt;if(!state.lastAppliedFiles||state.lastAppliedFiles.some(f=>['server.json','parameters.json','decoder.json'].includes(f)))await refreshCatalog();refreshPanels();window.dispatchEvent(new CustomEvent('configuration-applied',{detail:state}));toast('配置已生效，既有计数、记录和存档已保留');}
    }catch(e){if(app.user===owner){note.classList.remove('hidden');text.textContent='无法查询配置生效状态：'+e.message;}}
    finally{polling=false;}
  }
  function refreshPanels(){if(['settings','judgments','results','sequence-config','command-config','command-send'].includes(app.view))navigate(app.view,true);}
  cancel.addEventListener('click',async()=>{if(!confirm('取消全部尚未生效的配置修改？正在使用的配置和已保存数据保持原样。'))return;try{await api('configuration/cancel',{});await poll();await refreshCatalog();refreshPanels();toast('已取消待生效修改');}catch(e){toast(e.message,true);}});
  document.addEventListener('click',event=>{const link=event.target.closest('a[href^="/api/"]');if(link){const path=link.getAttribute('href').slice(5),next=readPath(path);if(next!==path)link.href='/api/'+next;}});
  setInterval(()=>{if(Date.now()>=next){next=Date.now()+(document.hidden?5000:1000);poll();}},500);
  return{readPath,poll};
})();
