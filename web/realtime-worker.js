'use strict';
// Share one server event stream among same-origin browser tabs.
const clients=new Map();const updates=new Map();let stream=null,latest=null,connected=false,session='';
function send(port,message){try{port.postMessage(message);}catch{clients.delete(port);}}
function broadcast(message){for(const port of clients.keys())send(port,message);}
function stop(){if(stream)stream.close();stream=null;latest=null;updates.clear();connected=false;}
function connect(){
  if(stream||!clients.size)return;
  const source=new EventSource('/api/events?readings=1');stream=source;
  source.onopen=()=>{if(stream!==source)return;connected=true;broadcast({type:'connection',connected:true});};
  source.onmessage=event=>{if(stream!==source)return;try{latest=JSON.parse(event.data);updates.clear();connected=true;broadcast({type:'state',state:latest});}catch{}};
  source.addEventListener('readings',event=>{
    if(stream!==source||!latest)return;
    try{const rows=JSON.parse(event.data);for(const row of rows)updates.set(row.id,row);broadcast({type:'readings',rows});}catch{}
  });
  source.onerror=()=>{if(stream!==source)return;connected=false;broadcast({type:'connection',connected:false});};
}
self.onconnect=event=>{
  const port=event.ports[0];port.start();
  port.onmessage=event=>{
    const message=event.data||{};
    if(message.type==='detach'){clients.delete(port);port.close();if(!clients.size){stop();session='';}return;}
    if(message.type!=='subscribe'&&message.type!=='ping')return;
    const key=String(message.session||'');if(!key)return;
    if(session&&session!==key){stop();broadcast({type:'reauth'});}
    session=key;const added=!clients.has(port);clients.set(port,Date.now());
    if(added||message.type==='subscribe'){send(port,{type:'connection',connected});if(latest){send(port,{type:'state',state:latest});if(updates.size)send(port,{type:'readings',rows:Array.from(updates.values())});}}
    connect();
  };
  port.onmessageerror=()=>{clients.delete(port);if(!clients.size)stop();};
};
// An inactive page can renew its subscription after background throttling or sleep.
setInterval(()=>{const cutoff=Date.now()-90000;for(const [port,seen] of clients)if(seen<cutoff)clients.delete(port);if(!clients.size){stop();session='';}},10000);
