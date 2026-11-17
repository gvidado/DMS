'use strict';
window.AlarmUI = (() => {
  const panel=element('section','panel');panel.id='alarm-history-panel';
  panel.innerHTML='<div class="panel-heading"><div><h2>异常判读记录</h2><p>常态化判读、序列执行、序列判读和SPP接收异常统一在“判读显示”中查看，填写原因后逐条解除。</p></div><button id="open-unified-abnormals" class="button">查看异常判读记录</button></div>';
  $('view-overview').prepend(panel);$('ack-alarms').textContent='查看异常记录';
  $('open-unified-abnormals').addEventListener('click',()=>window.UnifiedAbnormalUI?.open());
  function render(){ $('ack-alarms').disabled=false; }
  return {render,refresh:render,reset:render};
})();
