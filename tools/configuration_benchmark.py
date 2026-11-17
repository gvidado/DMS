"""Measure stopped configuration saves with a large synthetic telemetry catalog."""
import json,secrets,shutil,socket,subprocess,sys,tempfile,time,statistics
from pathlib import Path
from integration_test import ROOT,Client,free_port,seed_send_monitor

def main():
 exe=Path(sys.argv[1]).resolve();report=Path(sys.argv[2]).resolve()
 with tempfile.TemporaryDirectory(dir=ROOT/'test-output',prefix='configuration-bench-') as scratch:
  root=Path(scratch);shutil.copytree(ROOT/'config',root/'config');shutil.copytree(ROOT/'web',root/'web');port=free_port()
  for name in ['continuous-rules.json','tc-bindings.json','sequences.json']:(root/'config'/name).unlink(missing_ok=True)
  settings=json.loads((root/'config/server.json').read_text(encoding='utf-8-sig'));settings.update(BindAddress='127.0.0.1',UdpPort=free_port(socket.SOCK_DGRAM),WebPort=port,SourceIPs=['127.0.0.1'],MulticastGroup='',MulticastInterface='');(root/'config/server.json').write_text(json.dumps(settings),encoding='utf-8');seed_send_monitor(root)
  fields=[dict(Id='test/A'+str(i),Code='A'+str(i),Name='测试量'+str(i),Group='测试',Offset=i*2,Length=2,BitStart=-1,BitCount=0,Type='uint',Scale=1,Bias=0,Precision=0,Format='十进制',Unit='',Note='',Supported=True,Enums={},EnumRanges=[]) for i in range(1500)]
  (root/'config/decoder.json').write_text(json.dumps(dict(SchemaVersion=2,Revision='benchmark',Catalog=dict(Parameters=fields,Sheets=[dict(Name='测试',Index=2)]),Packets=[dict(Id='e',Name='测试包',OuterSourceId=2050,SourceIP='127.0.0.1',Enabled=True,MappingConfirmed=True,Apid=8,ExpectedLengthField=-1,Groups=['测试'])])),encoding='utf-8')
  proc=subprocess.Popen([str(exe),'--local','--root',str(root),'--web-port',str(port)],stdout=subprocess.DEVNULL,stderr=subprocess.STDOUT,creationflags=subprocess.CREATE_NO_WINDOW);c=Client(port)
  try:
   for _ in range(300):
    try:c.call('/api/auth/me');break
    except OSError:time.sleep(.05)
   password=secrets.token_urlsafe(16);c.call('/api/auth/setup',dict(username='benchmark',name='性能测试',password=password));c.csrf=c.call('/api/auth/login',dict(username='benchmark',password=password))['csrf'];loads=[];saves=[]
   for i in range(3):
    t=time.perf_counter();cfg=c.call('/api/config?draft=1');loads.append((time.perf_counter()-t)*1000);cfg['FramePeriodMs']=400+i;t=time.perf_counter();c.call('/api/config',cfg);saves.append((time.perf_counter()-t)*1000);assert c.call('/api/config')['FramePeriodMs']==400+i
   value=dict(parameters=1500,runs=3,editLoadMs=loads,saveMs=saves,medianSaveMs=statistics.median(saves),medianTotalMs=statistics.median([a+b for a,b in zip(loads,saves)]));report.write_text(json.dumps(value,indent=2),encoding='utf-8');print(json.dumps(value))
  finally:proc.terminate();proc.wait(8)

if __name__=='__main__':main()
