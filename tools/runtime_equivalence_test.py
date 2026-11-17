"""Compare deterministic receiver outputs from the released executable and optimized candidate."""
import json,secrets,shutil,socket,subprocess,sys,tempfile,time
from pathlib import Path
from integration_test import ROOT,Client,free_port,seed_send_monitor

def packet(source,sequence,value=979,length=26):
 b=bytearray(length);b[0:2]=source.to_bytes(2,'big');b[4:6]=(length-7).to_bytes(2,'big');b[6:8]=bytes.fromhex('0808');b[8:10]=(0xc000|sequence).to_bytes(2,'big');b[10:12]=(length-13).to_bytes(2,'big');b[12:16]=bytes([0x30,3,25,0]);b[18:20]=(4100).to_bytes(2,'big');b[22:24]=value.to_bytes(2,'big');return seal(b)
def seal(b):
 a=z=0
 for v in b[6:-2]:a=(a+v)%255;z=(z+a)%255
 b[-2]=(255-(a+z)%255)%255;b[-1]=(255-(a+b[-2])%255)%255;return b
def run(exe):
 with tempfile.TemporaryDirectory(dir=ROOT/'test-output',prefix='equivalence-') as scratch:
  root=Path(scratch);shutil.copytree(ROOT/'config',root/'config');shutil.copytree(ROOT/'web',root/'web');web=free_port();udp=free_port(socket.SOCK_DGRAM);c=Client(web)
  def save(name,value):(root/'config'/name).write_text(json.dumps(value),encoding='utf-8')
  for name in ['decoder.json','continuous-rules.json','tc-bindings.json','sequences.json','observed-commands.json']:(root/'config'/name).unlink(missing_ok=True)
  settings=json.loads((root/'config/server.json').read_text(encoding='utf-8-sig'));settings.update(BindAddress='127.0.0.1',UdpPort=udp,WebPort=web,SourceIPs=['127.0.0.1'],MulticastGroup='',MulticastInterface='',DataTimeoutMs=600000);save('server.json',settings);seed_send_monitor(root)
  fields=[dict(Id='test/A1',Code='A1',Name='对比量',Group='测试',Offset=0,Length=2,BitStart=-1,BitCount=0,Type='uint',Scale=.01,Bias=0,Precision=2,Format='十进制',Unit='V',Note='',Supported=True,Enums={},EnumRanges=[]),dict(Id='test/A2',Code='A2',Name='越界量',Group='测试',Offset=2,Length=2,BitStart=-1,BitCount=0,Type='uint',Scale=1,Bias=0,Precision=0,Format='十进制',Unit='',Supported=True,Enums={},EnumRanges=[])]
  packets=[dict(Id=k,Name=k+'包',OuterSourceId=source,SourceIP='127.0.0.1',Enabled=True,MonitorRaw=True,MappingConfirmed=True,Apid=8,ExpectedLengthField=1017,Groups=['测试']) for k,source in [('e',2050),('b',514)]]
  save('decoder.json',dict(SchemaVersion=2,Revision='equivalent',Catalog=dict(Parameters=fields,Sheets=[dict(Name='测试',Index=2)]),Packets=packets));script='InitRegion\n this.state=0;\nEndInitRegion\nStateRegion\nEndStateRegion\nResultRegion\n IF abs(this.value-9.8)<1 THEN RET=Normal; ELSE RET=Unusual; ENDIF;\nEndResultRegion';save('continuous-rules.json',dict(Revision='equivalent',Rules=[dict(Id='a'*32,Code='TME.A1',ParameterId='e::TME::test/A1',Name='对比量',SourceIP='127.0.0.1',Enabled=True,Level='一般',Script=script)]))
  proc=subprocess.Popen([str(exe),'--local','--root',str(root),'--web-port',str(web)],stdout=subprocess.DEVNULL,stderr=subprocess.STDOUT,creationflags=subprocess.CREATE_NO_WINDOW);sender=socket.socket(socket.AF_INET,socket.SOCK_DGRAM);sender.bind(('127.0.0.1',0));trace=[]
  def wait(fn,label):
   for _ in range(250):
    try:
     value=fn()
     if value:return value
    except OSError:pass
    time.sleep(.02)
   raise AssertionError(label)
  try:
   wait(lambda:c.call('/api/auth/me'),'ready');password=secrets.token_urlsafe(16);c.call('/api/auth/setup',dict(username='equivalence',name='行为对照',password=password));c.csrf=c.call('/api/auth/login',dict(username='equivalence',password=password))['csrf'];catalog=c.call('/api/catalog');c.call('/api/selection',dict(ids=[p['Id'] for p in catalog]));c.call('/api/control',dict(action='start'))
   cases=[('valid-e',packet(2050,1)),('valid-b',packet(514,1,1050)),('duplicate',packet(2050,1,800)),('bad-checksum',packet(2050,2)),('new-length',packet(2050,4,979,30)),('old-frame',packet(2050,3,970)),('ignored-secondary',packet(2050,5,980)),('wrong-routing',packet(2050,6,980)),('wrong-outer-length',packet(2050,7,980)),('unknown-source',packet(1234,1)),('valid-e-new',packet(2050,8,1000))]
   cases[3][1][-1]^=1;cases[6][1][12]^=0x40;seal(cases[6][1]);cases[7][1][13]=4;seal(cases[7][1]);cases[8][1][5]^=1
   for n,(label,raw) in enumerate(cases,1):
    sender.sendto(raw,('127.0.0.1',udp));state=wait(lambda:(s if (s:=c.call('/api/state'))['received']>=n else None),label);history=c.call('/api/telemetry/history?parameterId=e::TME::test/A1&sourceIP=127.0.0.1');raws=c.call('/api/raw-spp/list');
    trace.append(dict(case=label,counters={k:state[k] for k in ['received','accepted','duplicates','invalid','lost','outOfOrder','filtered']},rows=[{k:r[k] for k in ['id','value','hexSource','status','sequence']} for r in state['rows']],history=[None if sample is None else {k:sample[k] for k in ['valueText','hexSource','sequence','sampleNumber']} for sample in history['slots']],raw=[{k:r[k] for k in ['id','packetId','sequence','sppLength','udpLength','status','checksum','reason']} for r in raws['records']]))
   c.call('/api/control',dict(action='stop'));return trace
  finally:sender.close();proc.terminate();proc.wait(8)

def main():
 before=run(Path(sys.argv[1]).resolve());after=run(Path(sys.argv[2]).resolve());out=ROOT/'test-output/release-v0.9.7/equivalence-report.json';out.write_text(json.dumps(dict(cases=len(before),equivalent=before==after,before=before,after=after),ensure_ascii=False,indent=2),encoding='utf-8');assert before==after,'receiver outputs differ; inspect '+str(out);print('PASS',len(before),'receiver cases have identical values, histories, counters and raw diagnostics.')

if __name__=='__main__':main()
