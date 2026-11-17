"""Legacy destination duplicates merge into one payload while existing TC codes survive."""
import json,secrets,shutil,socket,subprocess,sys,tempfile
from pathlib import Path
from integration_test import ROOT,Client,free_port,wait,seed_send_monitor

def main():
    executable=Path(sys.argv[1]).resolve() if len(sys.argv)>1 else ROOT/'TelemetryMonitor.exe'
    with tempfile.TemporaryDirectory(prefix='command-merge-') as temporary:
        root=Path(temporary);shutil.copytree(ROOT/'config',root/'config');shutil.copytree(ROOT/'web',root/'web');seed_send_monitor(root);(root/'config/decoder.json').unlink(missing_ok=True)
        legacy=dict(SchemaVersion=1,SourceIP='127.0.0.1',IdentityRule='destination-ip-and-udp-payload',NextNumber=3,Commands=[dict(Number=i,Name='主指令' if i==1 else '另一目的的旧指令',DestinationIP=ip,PayloadHex='00 AA FF',Length=3,FirstSeenUtc='2026-11-02T00:00:00Z') for i,ip in [(1,'127.0.0.2'),(2,'127.0.0.3')]])
        path=root/'config/observed-commands.json';path.write_text(json.dumps(legacy,ensure_ascii=False),encoding='utf-8');original=path.read_bytes()
        tc=root/'config/tc-bindings.json';tc.write_text(json.dumps(dict(SchemaVersion=1,Revision='legacy-tc',Bindings=[dict(Code='K001',Number=1),dict(Code='K002',Number=2)])),encoding='utf-8');tc_bytes=tc.read_bytes()
        rules=[dict(Id=str(i),Code=code,Name=code,Level='一般',Enabled=True,ParameterId='工仪全程/'+code,SourceIP='127.0.0.1',Script='InitRegion this.state=0; EndInitRegion StateRegion if(TC.K00'+str(i)+') then this.state=this.state+1; end EndStateRegion ResultRegion RET=Normal; EndResultRegion') for i,code in [(1,'A1'),(2,'A4')]]
        (root/'config/continuous-rules.json').write_text(json.dumps(dict(SchemaVersion=1,Revision='merge-test',Rules=rules)),encoding='utf-8')
        settings=json.loads((root/'config/server.json').read_text(encoding='utf-8-sig'));settings.update(BindAddress='127.0.0.1',UdpPort=free_port(socket.SOCK_DGRAM),SourceIPs=['127.0.0.1'],MappingConfirmed=True,MulticastGroup='',MulticastInterface='');(root/'config/server.json').write_text(json.dumps(settings),encoding='utf-8')
        port=free_port();password=secrets.token_urlsafe(18)
        def launch():return subprocess.Popen([str(executable),'--local','--root',str(root),'--web-port',str(port)],stdout=subprocess.DEVNULL,stderr=subprocess.STDOUT)
        process=launch();c=Client(port)
        try:
            def ready():
                try:c.call('/api/auth/me');return True
                except OSError:return False
            def login():c.csrf=c.call('/api/auth/login',dict(username='mergeqa',password=password))['csrf']
            def disk():return json.loads(path.read_text(encoding='utf-8-sig'))
            def states():return [r['currentState'] for r in c.call('/api/continuous-rules')['rows']]
            wait(ready,'merge server');c.call('/api/auth/setup',dict(username='mergeqa',name='Merge QA',password=password));login()
            merged=disk();assert len(merged['Commands'])==1 and merged['IdentityRule']=='udp-payload';entry=merged['Commands'][0];assert entry['Number']==1 and entry['Name']=='主指令' and entry['DestinationIPs']==['127.0.0.2','127.0.0.3'] and entry['Aliases']==[dict(Number=2,Name='另一目的的旧指令')];assert tc.read_bytes()==tc_bytes and path.with_suffix('.json.bak').read_bytes()==original
            listing=c.call('/api/continuous-rules/tc?search=127.0.0.3');assert listing['total']==1 and listing['rows'][0]['codes']==['K001','K002'];assert c.call('/api/continuous-rules/tc/detail?number=2')['number']==1
            print('PASS old duplicate payloads merge destinations and names, retain TC numbers and preserve original backup')
            adapter=next(a for a in c.call('/api/send-monitor/interfaces')['interfaces'] if a['loopback']);c.call('/api/send-monitor/config',dict(SourceIPs=['127.0.0.1'],DestinationIPs=['127.0.0.4','127.0.0.5'],InterfaceId=adapter['id'],DestinationPort=0));c.call('/api/control',dict(action='start'));c.call('/api/continuous-rules/control',dict(action='start'))
            with socket.socket(socket.AF_INET,socket.SOCK_DGRAM) as sender,socket.socket(socket.AF_INET,socket.SOCK_DGRAM) as target,socket.socket(socket.AF_INET,socket.SOCK_DGRAM) as target2:
                sender.bind(('127.0.0.1',0));target.bind(('127.0.0.4',0));target2.bind(('127.0.0.5',0));target.settimeout(3);target2.settimeout(3)
                def send(payload,receiver=target):sender.sendto(payload,receiver.getsockname());assert receiver.recvfrom(65535)[0]==payload
                send(b'\x00\xaa\xff');wait(lambda:states()==[1,1],'both old TC aliases');assert len(disk()['Commands'])==1 and disk()['Commands'][0]['DestinationIPs']==['127.0.0.2','127.0.0.3','127.0.0.4'];before=path.read_bytes()
                send(b'\x00\xaa\xff');wait(lambda:states()==[2,2],'repeat TC');assert path.read_bytes()==before
                send(b'\x00\xaa\xff',target2);wait(lambda:states()==[3,3],'new target TC');assert len(disk()['Commands'])==1 and disk()['NextNumber']==3 and disk()['Commands'][0]['DestinationIPs'][-1]=='127.0.0.5'
                send(b'\xbb',target2);wait(lambda:len(disk()['Commands'])==2,'new payload');assert disk()['Commands'][1]['Number']==3 and disk()['Commands'][1]['Name']=='指令3'
            print('PASS same payload to unregistered new destinations keeps one identity and all TC aliases; new payload uses next unused number')
            c.call('/api/control',dict(action='stop'));before=path.read_bytes();process.terminate();process.wait(timeout=6);process=launch();c=Client(port);wait(ready,'merge restart');login();assert path.read_bytes()==before and tc.read_bytes()==tc_bytes and next(row for row in c.call('/api/continuous-rules/tc')['rows'] if row['number']==1)['codes']==['K001','K002']
            print('PASS merged destinations, aliases and custom command name persist without rewrites across restart')
        finally:
            if process.poll() is None:process.terminate();process.wait(timeout=6)

if __name__=='__main__':main()
