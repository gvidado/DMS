"""Prepare isolated synthetic runtime fixtures and run the same measurement harness before/after."""
import json,shutil,subprocess,sys,tempfile
from pathlib import Path
from integration_test import ROOT

def seed(root,per_packet=250,packets=6):
 (root/'config').mkdir(parents=True,exist_ok=True);shutil.copy(ROOT/'config/server.json',root/'config/server.json');(root/'config/parameters.json').write_text('[]',encoding='utf-8')
 settings=json.loads((root/'config/server.json').read_text(encoding='utf-8-sig'));settings.update(BindAddress='127.0.0.1',UdpPort=41237,SourceIPs=['127.0.0.1'],MulticastGroup='',MulticastInterface='',DataTimeoutMs=600000);(root/'config/server.json').write_text(json.dumps(settings),encoding='utf-8');(root/'config/send-monitor.json').write_text(json.dumps(dict(SourceIP='127.0.0.1',DestinationIP='127.0.0.2',DestinationPort=0,InterfaceId='')),encoding='utf-8')
 fields=[];definitions=[];rules=[]
 for packet in range(packets):
  group='表'+str(packet);pid='p'+str(packet)
  definitions.append(dict(Id=pid,Name=group,Enabled=True,MonitorRaw=True,MappingConfirmed=True,OuterSourceId=2050,SourceIP='127.0.0.1',Apid=8+packet,ExpectedLengthField=-1,Groups=[group]))
  for i in range(per_packet):
   ordinal=packet*per_packet+i;code='A'+str(ordinal);fid=group+'/'+code
   fields.append(dict(Id=fid,Code=code,Name='测试量'+str(ordinal),Group=group,Offset=i*2,Length=2,BitStart=-1,BitCount=0,Type='uint',Scale=1,Bias=0,Precision=0,Format='十进制',Supported=True,Enums={},EnumRanges=[]))
   rules.append(dict(Id=f'{ordinal+1:032x}',ParameterId=pid+'::TME::'+fid,Code='TME.'+code,Name='测试量'+str(ordinal),SourceIP='127.0.0.1',Level='一般',Enabled=True,Script='InitRegion\n this.state=0;\nEndInitRegion\nStateRegion\nEndStateRegion\nResultRegion\n RET=Normal;\nEndResultRegion',Bindings={}))
 (root/'config/decoder.json').write_text(json.dumps(dict(SchemaVersion=2,Revision='runtime-fixture',Catalog=dict(Parameters=fields,Sheets=[dict(Name='表'+str(i),Index=i+2) for i in range(packets)]),Packets=definitions)),encoding='utf-8')
 (root/'config/continuous-rules.json').write_text(json.dumps(dict(SchemaVersion=1,Revision='runtime-rules',Rules=rules)),encoding='utf-8');return per_packet

def main():
 harness=Path(sys.argv[1]).resolve();report=Path(sys.argv[2]).resolve()
 with tempfile.TemporaryDirectory(dir=ROOT/'test-output',prefix='runtime-bench-') as scratch:
  root=Path(scratch);count=seed(root);result=subprocess.run([str(harness),str(root),str(count)],capture_output=True,text=True,encoding='utf-8',timeout=180,creationflags=subprocess.CREATE_NO_WINDOW);print(result.stdout,flush=True)
  if result.returncode:raise RuntimeError(result.stderr)
  parsed=json.loads(result.stdout.strip().splitlines()[-1]);report.write_text(json.dumps(parsed,indent=2),encoding='utf-8')

if __name__=='__main__':main()
