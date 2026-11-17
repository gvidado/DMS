"""Exercise pending configuration through real HTTP/UDP; production uses the 120-second timer."""
import json,secrets,shutil,socket,subprocess,sys,tempfile,threading,time
from pathlib import Path
from integration_test import ROOT,Client,free_port

def main():
    exe=Path(sys.argv[1]).resolve();host=Path(sys.argv[2]).resolve() if len(sys.argv)>2 else None;checks=[]
    def check(ok,label):assert ok,label;checks.append(label);print('PASS',label,flush=True)
    def until(fn,label,timeout=10):
        end=time.monotonic()+timeout
        while time.monotonic()<end:
            value=fn()
            if value:return value
            time.sleep(.04)
        raise AssertionError('Timeout: '+label)
    with tempfile.TemporaryDirectory(dir=ROOT/'test-output',prefix='scheduled-config-') as scratch:
        root=Path(scratch);shutil.copytree(ROOT/'config',root/'config');shutil.copytree(ROOT/'web',root/'web')
        for name in ['decoder.json','continuous-rules.json','tc-bindings.json','sequences.json','observed-commands.json','send-monitor.json','command-send.json']:(root/'config'/name).unlink(missing_ok=True)
        udp_port=free_port(socket.SOCK_DGRAM);new_udp_port=free_port(socket.SOCK_DGRAM);port=free_port();source_port=free_port(socket.SOCK_DGRAM);new_source_port=free_port(socket.SOCK_DGRAM)
        receiver=socket.socket(socket.AF_INET,socket.SOCK_DGRAM);receiver.bind(('127.0.0.2',0));receiver.settimeout(6);new_receiver=socket.socket(socket.AF_INET,socket.SOCK_DGRAM);new_receiver.bind(('127.0.0.2',0));new_receiver.settimeout(6)
        settings=json.loads((root/'config/server.json').read_text(encoding='utf-8-sig'));settings.update(BindAddress='127.0.0.1',UdpPort=udp_port,WebPort=port,SourceIPs=['127.0.0.1'],MulticastGroup='',MulticastInterface='',DataTimeoutMs=30000);(root/'config/server.json').write_text(json.dumps(settings),encoding='utf-8')
        fields=[dict(Id='测试/A1',Code='A1',Name='量1',Group='测试',Offset=0,Length=2,BitStart=-1,BitCount=0,Type='uint',Scale=1,Bias=0,Precision=0,Format='十进制',Unit='V',Note='',Supported=True,Enums={},EnumRanges=[])]
        packet=dict(Id='e',Name='旧包名',OuterSourceId=0x0802,SourceIP='127.0.0.1',Enabled=True,MonitorRaw=True,MappingConfirmed=True,Apid=8,ExpectedLengthField=1017,Groups=['测试'])
        (root/'config/decoder.json').write_text(json.dumps(dict(SchemaVersion=2,Revision='original-decoder',Catalog=dict(Parameters=fields,Sheets=[dict(Name='测试',Index=2)]),Packets=[packet])),encoding='utf-8')
        old_script='InitRegion\n this.state=0;\nEndInitRegion\nStateRegion\nEndStateRegion\nResultRegion\n RET=Unusual;\nEndResultRegion'
        rule=dict(Id='a'*32,ParameterId='e::TME::测试/A1',Code='TME.A1',Name='量1',Level='一般',SourceIP='127.0.0.1',Enabled=True,Script=old_script,Bindings={})
        (root/'config/continuous-rules.json').write_text(json.dumps(dict(SchemaVersion=1,Revision='original-rules',Rules=[rule])),encoding='utf-8')
        (root/'config/tc-bindings.json').write_text(json.dumps(dict(SchemaVersion=1,Revision='original-tc',Bindings=[dict(Number=1,Code='K001')])),encoding='utf-8')
        command=dict(Number=1,Name='旧指令名',DestinationIP='127.0.0.2',DestinationIPs=['127.0.0.2'],PayloadHex='A1 01',Length=2,Categories=[])
        (root/'config/observed-commands.json').write_text(json.dumps(dict(SchemaVersion=1,SourceIP='127.0.0.1',IdentityRule='udp-payload',NextNumber=2,Commands=[command])),encoding='utf-8')
        (root/'config/send-monitor.json').write_text(json.dumps(dict(SourceIP='127.0.0.1',DestinationIP='127.0.0.2',InterfaceId='',DestinationPort=0)),encoding='utf-8')
        (root/'config/command-send.json').write_text(json.dumps(dict(SourceIP='127.0.0.1',Routes=[dict(DestinationIP='127.0.0.2',SourcePort=source_port,DestinationPort=receiver.getsockname()[1])])),encoding='utf-8')
        rows=[dict(Row=2,Keyword='TEXT',Description='已完成步骤',Expected='F:0'),dict(Row=3,Keyword='',Description='指令后等待',Command='K001',Condition='TME.A1.value==999',Expected='.2',Timeout='180'),dict(Row=4,Keyword='',Description='后续指令',Command='K001',Expected='.2',Timeout='180')]
        definition=dict(Id='b'*32,Code='XLZ0001',Name='热更新执行',Mode='execute',SourceIP='127.0.0.1',Rows=rows)
        (root/'config/sequences.json').write_text(json.dumps(dict(SchemaVersion=1,Revision='original-sequences',DefaultTimeoutSeconds=180,Sequences=[definition],NextExecuteNumber=2,NextJudgeNumber=1)),encoding='utf-8')
        args=[str(host),str(root),str(port)] if host else [str(exe),'--local','--root',str(root),'--web-port',str(port)]
        started=time.monotonic();proc=subprocess.Popen(args,stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.STDOUT,text=True,creationflags=subprocess.CREATE_NO_WINDOW);c=Client(port);stop=threading.Event();sender=socket.socket(socket.AF_INET,socket.SOCK_DGRAM);sender.bind(('127.0.0.1',0));destination=[udp_port];thread=None
        def traffic():
            sequence=0
            while not stop.is_set():
                sequence=(sequence+1)%16384;b=bytearray(26);b[0:2]=bytes.fromhex('0802');b[4:6]=(19).to_bytes(2,'big');b[6:8]=bytes.fromhex('0808');b[8:10]=(0xc000|sequence).to_bytes(2,'big');b[10:12]=(13).to_bytes(2,'big');b[12:16]=bytes([0x30,3,25,0]);b[18:20]=(0x1004).to_bytes(2,'big');b[22:24]=(10).to_bytes(2,'big');a=z=0
                for v in b[6:-2]:a=(a+v)%255;z=(z+a)%255
                b[-2]=(255-(a+z)%255)%255;b[-1]=(255-(a+b[-2])%255)%255;sender.sendto(b,('127.0.0.1',destination[0]));stop.wait(.1)
        try:
            def ready():
                try:return c.call('/api/auth/me')
                except OSError:return False
            until(ready,'server');password=secrets.token_urlsafe(16);c.call('/api/auth/setup',dict(username='cfgqa',name='配置测试',password=password));c.csrf=c.call('/api/auth/login',dict(username='cfgqa',password=password))['csrf'];c.call('/api/control',dict(action='start'));thread=threading.Thread(target=traffic,daemon=True);thread.start();until(lambda:c.call('/api/state')['accepted']>=2,'telemetry');c.call('/api/continuous-rules/control',dict(action='start'))
            cfg=c.call('/api/sequence/config');run=c.call('/api/sequence/control',dict(action='start',id=definition['Id'],runName='运行中切换',requestId=secrets.token_hex(16),revision=cfg['revision']))['runId'];check(receiver.recvfrom(100)[0]==bytes.fromhex('A101'),'original running sequence sends its first instruction')
            state=lambda:c.call('/api/sequence/state?runId='+run);until(lambda:state()['rows'][1]['status']=='等待参数','pending predicate');saved_first=state()['rows'][0];old_sent=c.call('/api/command-send/history')['rows'][0]
            until(lambda:c.call('/api/continuous-rules/abnormals')['records'],'original abnormal');old_abnormal=c.call('/api/continuous-rules/abnormals')['records'][0]
            effective_decoder=c.call('/api/decoders');draft=c.call('/api/decoders?draft=1');parameter=dict(draft['configuration']['Catalog']['Parameters'][0]);parameter['Scale']=2;c.call('/api/decoders/parameter',dict(id=parameter['Id'],revision=draft['revision'],parameter=parameter));until(lambda:c.call('/api/configuration/schedule')['pending'],'pending parameter')
            check(c.call('/api/decoders')['configuration']['Catalog']['Parameters'][0]['Scale']==1 and c.call('/api/decoders?draft=1')['configuration']['Catalog']['Parameters'][0]['Scale']==2,'online parameter edits are staged while effective decoding stays unchanged')
            draft=c.call('/api/decoders?draft=1');p=draft['configuration']['Packets'][0];p['Name']='新包名';c.call('/api/decoders/packet',dict(packet=p,revision=draft['revision']))
            rules=c.call('/api/continuous-rules?draft=1');edit=next(r['rule'] for r in rules['rows'] if r['rule']['Id']==rule['Id']);edit['Script']=old_script.replace('RET=Unusual','RET=Normal');c.call('/api/continuous-rules/save',dict(revision=rules['revision'],rule=edit))
            library=c.call('/api/send-monitor/commands/list?draft=1');c.call('/api/send-monitor/commands/edit',dict(number=1,revision=library['revision'],originalCode='',code='',name='新指令名'))
            tc=c.call('/api/continuous-rules/tc?draft=1');c.call('/api/continuous-rules/tc',dict(number=1,code='K002',revision=tc['revision']))
            cfg=c.call('/api/sequence/config?draft=1');preview=c.call('/api/sequence/edit-preview',dict(id=definition['Id'],revision=cfg['revision']));item=preview['definitions'][0]['definition']
            for row in item['Rows']:
                if row['Command']:row['Command']='K002'
                if row['Row']==3:row['Condition']='TME.A1.value==20'
            checked=c.call('/api/sequence/recheck',dict(token=preview['token'],id=item['Id'],name=item['Name'],rows=item['Rows']));assert not checked['definitions'][0]['errors'],checked;c.call('/api/sequence/save-edits',dict(token=checked['token']))
            config=c.call('/api/config?draft=1');config['UdpPort']=new_udp_port;c.call('/api/config',config)
            monitor=c.call('/api/send-monitor/state?draft=1')['configuration'];monitor['DestinationPort']=new_receiver.getsockname()[1];response=c.call('/api/send-monitor/config',monitor);check(response['total']>=1,'saving capture settings preserves the displayed live counters')
            tx=c.call('/api/command-send/config?draft=1');tx['configuration']['Routes']=[dict(DestinationIP='127.0.0.2',SourcePort=new_source_port,DestinationPort=new_receiver.getsockname()[1])];c.call('/api/command-send/config',dict(configuration=tx['configuration'],revision=tx['revision']))
            pending=c.call('/api/configuration/schedule');check(len(pending['files'])==8 and pending['intervalSeconds']==120,'all eight business configuration files join one 120-second batch')
            check(c.call('/api/state')['mode']=='live' and state()['runtime']['running'] and c.call('/api/continuous-rules')['execution']['running'],'configuration editing does not stop reception or active judgments and sequences')
            check(c.call('/api/config')['UdpPort']==udp_port and c.call('/api/sequence/definition?id='+definition['Id'])['definition']['Rows'][1]['Condition'].endswith('999'),'effective transport and sequence rules remain original before the batch')
            sender.sendto(bytes.fromhex('A1FF'),receiver.getsockname());assert receiver.recvfrom(100)[0]==bytes.fromhex('A1FF');until(lambda:c.call('/api/send-monitor/commands/list')['total']>=2,'learned instruction');before=c.call('/api/state');before_monitor=c.call('/api/send-monitor/state')['total']
            if host:
                proc.stdin.write('tick\n');proc.stdin.flush()
            else:
                print('Waiting for the actual 120-second schedule; telemetry remains active.',flush=True)
            def applied():
                status=c.call('/api/configuration/schedule')
                if status['error']:raise AssertionError(status['error'])
                return not status['pending'] and status['lastAppliedAt']
            until(applied,'scheduled application',135);destination[0]=new_udp_port
            if not host:check(118<=time.monotonic()-started<=140,'the real production timer waits for the 120-second boundary')
            until(lambda:any(r['value']=='20' for r in c.call('/api/state')['rows']),'new coefficient');check(c.call('/api/state')['accepted']>=before['accepted'] and c.call('/api/state')['received']>=before['received'],'batch application retains reception counters')
            check(c.call('/api/state')['mode']=='live' and c.call('/api/config')['UdpPort']==new_udp_port,'UDP moves to its new port without the global listener entering stopped state')
            check(new_receiver.recvfrom(100)[0]==bytes.fromhex('A101'),'the subsequent instruction uses the newly configured destination port');until(lambda:not state()['runtime']['running'],'sequence finish')
            history=c.call('/api/command-send/history')['rows'];check(len(history)==2 and next(x for x in history if x['id']==old_sent['id'])==old_sent,'already-sent instructions are not repeated or rewritten')
            check(state()['rows'][0]==saved_first and state()['rows'][1]['input']['Condition']=='TME.A1.value==20' and state()['runtime']['abnormal']==0,'completed steps remain original while the current predicate switches live')
            check(c.call('/api/send-monitor/state')['total']>=before_monitor and c.call('/api/send-monitor/commands/list')['total']==2,'capture records and commands learned during editing survive the configuration merge')
            check(c.call('/api/continuous-rules')['execution']['running'] and next(r for r in c.call('/api/continuous-rules')['rows'] if r['rule']['Id']==rule['Id'])['rule']['Script'].find('RET=Normal')>=0,'continuous judgment switches rules without restarting its run')
            incidents=c.call('/api/continuous-rules/abnormals')['records'];check(next(r for r in incidents if r['id']==old_abnormal['id'])==old_abnormal,'old abnormal values, scripts and release state are unchanged')
            archives=list((root/'data/configuration-data').glob('*.jsonl'));check(bool(archives) and '"Value":"10"' in ''.join(p.read_text(encoding='utf-8') for p in archives),'old decoded readings are archived before changed live fields await new data')
            check(not (root/'data/pending-configuration/pending.json').exists(),'applied staging state is cleared rather than retained as configuration versions')
            c.call('/api/control',dict(action='stop'));report=(exe.parent if exe.is_relative_to(ROOT/'test-output') else ROOT/'test-output/configuration-timing')/('real-timer-report.json' if not host else 'integration-report.json');report.write_text(json.dumps(dict(checks=len(checks),details=checks),ensure_ascii=False,indent=2),encoding='utf-8');print('All',len(checks),'scheduled configuration checks passed.')
        finally:
            stop.set()
            if thread:thread.join(2)
            sender.close();receiver.close();new_receiver.close()
            if proc.poll() is None:proc.terminate();proc.wait(timeout=8)

if __name__=='__main__':main()
