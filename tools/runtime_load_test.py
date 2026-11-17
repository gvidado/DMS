"""Seven independent browser-style SSE sessions while receiving and judging loopback UDP."""
import json,secrets,socket,subprocess,sys,tempfile,threading,time,urllib.request
from pathlib import Path
from integration_test import ROOT,Client,free_port
from runtime_benchmark import seed

def packet(fields,index,sequence):
 b=bytearray(24+fields*2)
 for at,value in [(0,2050),(4,len(b)-7),(6,0x0808+index),(8,0xc000|sequence),(10,len(b)-13),(18,4100)]:b[at:at+2]=value.to_bytes(2,'big')
 b[12:15]=bytes([48,3,25])
 for i in range(fields):b[22+i*2:24+i*2]=(100).to_bytes(2,'big')
 a=z=0
 for v in b[6:-2]:a=(a+v)%255;z=(z+a)%255
 b[-2]=(255-(a+z)%255)%255;b[-1]=(255-(a+b[-2])%255)%255;return b

def main():
 exe=Path(sys.argv[1]).resolve();out=ROOT/'test-output/release-v0.9.7/load-report.json'
 with tempfile.TemporaryDirectory(dir=ROOT/'test-output',prefix='runtime-load-') as scratch:
  root=Path(scratch);fields=seed(root);port=free_port();udp=free_port(socket.SOCK_DGRAM);settings=json.loads((root/'config/server.json').read_text(encoding='utf-8-sig'));settings['UdpPort']=udp;(root/'config/server.json').write_text(json.dumps(settings),encoding='utf-8');proc=subprocess.Popen([str(exe),'--local','--root',str(root),'--web-port',str(port)],stdout=subprocess.DEVNULL,stderr=subprocess.STDOUT,creationflags=subprocess.CREATE_NO_WINDOW);admin=Client(port);stop=threading.Event();workers=[];clients=[];stats=[dict(frames=0,lastAccepted=0) for _ in range(7)];errors=[];sender=socket.socket(socket.AF_INET,socket.SOCK_DGRAM)
  def until(fn,label,seconds=15):
   end=time.monotonic()+seconds
   while time.monotonic()<end:
    try:
     value=fn()
     if value:return value
    except OSError:pass
    time.sleep(.025)
   raise AssertionError(label)
  def read(index,client):
   try:
    with client.opener.open(client.base+'/api/events',timeout=8) as stream:
     while not stop.is_set():
      line=stream.readline()
      if line.startswith(b'data: '):
       state=json.loads(line[6:]);assert len(state['rows'])==1500;stats[index]['frames']+=1;stats[index]['lastAccepted']=state['accepted']
   except Exception as e:
    if not stop.is_set():errors.append(str(e))
  try:
   until(lambda:admin.call('/api/auth/me'),'server');password=secrets.token_urlsafe(16);admin.call('/api/auth/setup',dict(username='loadadmin',name='负载验证',password=password));admin.csrf=admin.call('/api/auth/login',dict(username='loadadmin',password=password))['csrf'];ids=[p['Id'] for p in admin.call('/api/catalog')]
   for i in range(7):
    username='loaduser'+str(i);admin.call('/api/users',dict(username=username,name='验证'+str(i),password=password));client=Client(port);client.csrf=client.call('/api/auth/login',dict(username=username,password=password))['csrf'];client.call('/api/selection',dict(ids=ids));clients.append(client)
   admin.call('/api/control',dict(action='start'));admin.call('/api/continuous-rules/control',dict(action='start'))
   for i,client in enumerate(clients):thread=threading.Thread(target=read,args=(i,client),daemon=True);workers.append(thread);thread.start()
   until(lambda:all(s['frames']>0 for s in stats),'all streams connected');started=time.monotonic();sequences=[0]*6;total=2400;rate=400
   for i in range(total):
    slot=i%6;sequences[slot]+=1;sender.sendto(packet(fields,slot,sequences[slot]),('127.0.0.1',udp));due=started+(i+1)/rate;remaining=due-time.monotonic()
    if remaining>0:time.sleep(remaining)
   until(lambda:admin.call('/api/state')['received']>=total,'all packets received',seconds=20);state=admin.call('/api/state');assert state['accepted']==total and state['invalid']==state['lost']==state['duplicates']==0,state;until(lambda:all(s['lastAccepted']==total for s in stats),'all browsers updated');assert not errors,errors
   raw=admin.call('/api/raw-spp/list');assert raw['retained']==1200 and all(p['retained']==200 for p in raw['packets']),raw
   rules=admin.call('/api/continuous-rules');assert rules['execution']['running'] and all(r['result']=='Normal' for r in rules['rows']);report=dict(parameters=1500,enabledRules=1500,clients=7,sent=total,received=state['received'],accepted=state['accepted'],invalid=state['invalid'],lost=state['lost'],duplicates=state['duplicates'],requestedRate=rate,seconds=time.monotonic()-started,streams=stats,rawRetained=raw['retained'],errors=errors);out.write_text(json.dumps(report,ensure_ascii=False,indent=2),encoding='utf-8');print(json.dumps(report,ensure_ascii=False));print('PASS seven clients, 2400 real UDP packets, continuous judgment and bounded raw retention.')
  finally:
   stop.set();sender.close()
   for worker in workers:worker.join(2)
   proc.terminate();proc.wait(8)

if __name__=='__main__':main()
