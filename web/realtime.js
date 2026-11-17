'use strict';
window.RealtimeUI={start(user){
  let active=true,worker=null,port=null,heartbeat=null,watchdog=null,pollTimer=null,polling=false,authChecking=false,fallback=false;
  const alive=()=>active&&app.user===user;
  function clearWatchdog(){clearTimeout(watchdog);watchdog=null;}
  function stopWorker(){clearInterval(heartbeat);heartbeat=null;if(port){try{port.postMessage({type:'detach'});port.close();}catch{}port=null;}if(worker){worker.onerror=null;worker=null;}}
  function deliver(state){
    if(!alive()||!state||!Array.isArray(state.rows))return;
    clearWatchdog();
    if(!app.selectionSaving){const selected=state.rows.map(row=>row.id),current=new Set(user.selected);if(selected.length!==current.size||selected.some(id=>!current.has(id))){user.selected=selected;if(!$('parameter-dialog').open)app.selection=new Set(selected);}}
    app.state=state;setConnection(true);renderState();
  }
  async function checkAuth(){
    if(!alive()||authChecking)return;authChecking=true;
    try{const info=await api('auth/me');if(!alive())return;if(!info.user)signedOut('登录已过期，请重新登录');else if(info.user.username!==user.username||info.user.csrf!==user.csrf||info.user.admin!==user.admin)await enter(info.user);}
    catch{}finally{authChecking=false;}
  }
  async function poll(){
    if(!alive()||!fallback||polling)return;polling=true;
    try{deliver(await api('state'));}catch{if(alive()){setConnection(false);checkAuth();}}
    finally{polling=false;if(alive()&&fallback)pollTimer=setTimeout(poll,document.hidden?2000:250);}
  }
  function startPolling(){if(!alive()||fallback)return;fallback=true;clearWatchdog();stopWorker();poll();}
  function workerMessage(event){
    if(!alive()||fallback)return;const message=event.data||{};
    if(message.type==='state')deliver(message.state);
    else if(message.type==='reauth')checkAuth();
    else if(message.type==='connection'){
      setConnection(!!message.connected);
      if(message.connected)clearWatchdog();
      else{checkAuth();if(!watchdog)watchdog=setTimeout(startPolling,4000);}
    }
  }
  if(typeof SharedWorker==='function'){
    try{
      worker=new SharedWorker('/realtime-worker.js',{name:'telemetry-realtime-v1'});port=worker.port;port.onmessage=workerMessage;port.onmessageerror=startPolling;
      worker.onerror=event=>{event.preventDefault();startPolling();};port.start();port.postMessage({type:'subscribe',session:user.csrf});
      heartbeat=setInterval(()=>{if(alive()&&port)port.postMessage({type:'ping',session:user.csrf});},20000);watchdog=setTimeout(startPolling,4000);
    }catch{startPolling();}
  }else startPolling();
  function visibility(){if(!alive())return;if(fallback&&!polling){clearTimeout(pollTimer);poll();}else if(port)port.postMessage({type:'ping',session:user.csrf});checkAuth();}
  function close(){if(!active)return;active=false;clearWatchdog();clearTimeout(pollTimer);stopWorker();document.removeEventListener('visibilitychange',visibility);window.removeEventListener('pagehide',close);}
  document.addEventListener('visibilitychange',visibility);window.addEventListener('pagehide',close);
  return {close};
}};
