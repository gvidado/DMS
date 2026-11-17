"""Authenticated live diagnostics and durable result evidence; loopback only."""
import json,secrets,shutil,socket,subprocess,sys,tempfile
from pathlib import Path
from integration_test import ROOT,Client,free_port,wait,packet,seal,seed_send_monitor

def main():
    exe=Path(sys.argv[1]).resolve()
    with tempfile.TemporaryDirectory(prefix='sequence-diagnostic-',dir=ROOT/'test-output') as scratch:
        root=Path(scratch);shutil.copytree(ROOT/'config',root/'config');shutil.copytree(ROOT/'web',root/'web')
        for f in ['decoder.json','continuous-rules.json','tc-bindings.json','sequences.json','observed-commands.json','command-send.json']:(root/'config'/f).unlink(missing_ok=True)
        seed_send_monitor(root);udp=free_port(socket.SOCK_DGRAM);web=free_port();c=Client(web);identifier='1'*32
        def save(name,value):(root/'config'/name).write_text(json.dumps(value),encoding='utf-8')
        cfg=json.loads((root/'config/server.json').read_text(encoding='utf-8-sig'));cfg.update(BindAddress='127.0.0.1',UdpPort=udp,SourceIPs=['127.0.0.1','127.0.0.2'],MappingConfirmed=True,MulticastGroup='',MulticastInterface='',DataTimeoutMs=60000);save('server.json',cfg)
        save('sequences.json',dict(SchemaVersion=1,Revision='diagnostic',DefaultTimeoutSeconds=60,Sequences=[dict(Id=identifier,Code='XLZ0001',Name='simple abs diagnosis',Mode='execute',SourceIP='127.0.0.1',Rows=[dict(Row=2,Keyword='',Command='',Condition='abs(TM.A1.value - 9.8) < 1',Expected='.2',Timeout='60')])]))
        proc=subprocess.Popen([str(exe),'--local','--root',str(root),'--web-port',str(web)],stdout=subprocess.DEVNULL,stderr=subprocess.STDOUT,creationflags=subprocess.CREATE_NO_WINDOW)
        senders=[];seq=0
        def ready():
            try:c.call('/api/auth/me');return True
            except OSError:return False
        def state():return c.call('/api/sequence/state?id='+identifier)
        def data(value,ip):
            nonlocal seq
            seq+=1;raw=packet(seq);raw[22:27]=round(value*10000).to_bytes(5,'big');s=socket.socket(socket.AF_INET,socket.SOCK_DGRAM);s.bind((ip,0));senders.append(s);s.sendto(seal(raw),('127.0.0.1',udp))
            wait(lambda:(c.call('/api/telemetry/history?reference=TM.A1&sourceIP='+ip)['current'] or {}).get('sequence')==seq,'decoded telemetry')
        try:
            wait(ready,'diagnostic server');password=secrets.token_urlsafe(18);c.call('/api/auth/setup',dict(username='diagnosticqa',name='Diagnostic QA',password=password));c.csrf=c.call('/api/auth/login',dict(username='diagnosticqa',password=password))['csrf'];c.call('/api/control',dict(action='start'))
            c.call('/api/sequence/control',dict(action='start',mode='execute',id=identifier,runName='simple abs diagnosis',requestId=secrets.token_hex(16),revision=c.call('/api/sequence/config')['revision']));wait(lambda:state()['rows'][0]['actual'] is not None,'step activation')
            data(0,'127.0.0.1');data(9.79,'127.0.0.2')
            diagnostic=c.call('/api/sequence/diagnostic?id='+identifier);assert diagnostic['rows'][0]['parameters'][0]['displaySourceMatchesBinding'] is False
            assert state()['rows'][0]['evaluation']['count']==1
            if len(sys.argv)>3:
                node=Path(sys.argv[2]);modules=Path(sys.argv[3]);settings=root/'browser.json';settings.write_text(json.dumps(dict(base=c.base,username='diagnosticqa',password=password,id=identifier,output=str(ROOT/'test-output/sequence-evaluation-fix/downloaded-diagnostic.json'))),encoding='utf-8')
                subprocess.run([str(node),str(ROOT/'tools/sequence_diagnostic_browser.mjs'),str(modules),str(settings)],check=True,timeout=35)
            data(9.79,'127.0.0.1');wait(lambda:not state()['runtime']['running'],'simple condition passes');assert state()['rows'][0]['evaluation']['result']=='满足'
            events=c.call('/api/sequence/history?runId='+state()['runtime']['runId'])['rows'];step=next(e for e in events if e['Event']=='步骤结果');assert step['Diagnostic']['lastEvaluationResult']=='满足' and step['Diagnostic']['usedReferences']==['TM.A1.value']
            saved=json.dumps(step['Diagnostic'],sort_keys=True);data(0,'127.0.0.1');events=c.call('/api/sequence/history?runId='+state()['runtime']['runId'])['rows'];assert json.dumps(next(e for e in events if e['Event']=='步骤结果')['Diagnostic'],sort_keys=True)==saved
            c.call('/api/control',dict(action='stop'));print('PASS authenticated diagnostics, precise simple-condition result and immutable archived source/input evidence')
        finally:
            for s in senders:s.close()
            proc.terminate();proc.wait(timeout=8)

if __name__=='__main__':main()
