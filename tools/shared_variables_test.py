"""Live shared-variable edits, dependent dispatch, value types and session reset."""
import json,secrets,shutil,socket,subprocess,sys,tempfile
from pathlib import Path
from integration_test import ROOT,Client,free_port,packet,seal,seed_send_monitor,wait

def main():
    executable=Path(sys.argv[1]).resolve() if len(sys.argv)>1 else ROOT/'TelemetryMonitor.exe'
    checks=[]
    def check(ok,label):assert ok,label;checks.append(label);print('PASS',label,flush=True)
    with tempfile.TemporaryDirectory(prefix='shared-variables-') as temporary:
        root=Path(temporary);shutil.copytree(ROOT/'config',root/'config');shutil.copytree(ROOT/'web',root/'web');seed_send_monitor(root)
        for name in ['decoder.json','continuous-rules.json','tc-bindings.json']:(root/'config'/name).unlink(missing_ok=True)
        port=free_port();udp=free_port(socket.SOCK_DGRAM);settings=json.loads((root/'config/server.json').read_text(encoding='utf-8-sig'));settings.update(BindAddress='127.0.0.1',UdpPort=udp,SourceIPs=['127.0.0.1'],MappingConfirmed=True,DataTimeoutMs=10000,MulticastGroup='',MulticastInterface='');(root/'config/server.json').write_text(json.dumps(settings),encoding='utf-8')
        process=subprocess.Popen([str(executable),'--local','--root',str(root),'--web-port',str(port)],stdout=subprocess.DEVNULL,stderr=subprocess.STDOUT);sender=socket.socket(socket.AF_INET,socket.SOCK_DGRAM);sender.bind(('127.0.0.1',0));seq=0;c=Client(port)
        try:
            def ready():
                try:return c.call('/api/auth/me')['setupRequired']
                except OSError:return False
            wait(ready,'shared-variable server');c.call('/api/continuous-rules/globals',expected=401)
            password=secrets.token_urlsafe(18);c.call('/api/auth/setup',dict(username='sharedqa',name='共享变量测试',password=password));c.csrf=c.call('/api/auth/login',dict(username='sharedqa',password=password))['csrf']
            def rules():return c.call('/api/continuous-rules')
            def info():return c.call('/api/continuous-rules/globals')
            def variables():return {r['name']:r for r in info()['rows']}
            def result():return rules()['rows'][0]['result']
            def control(action,expected=200):return c.call('/api/continuous-rules/control',dict(action=action),expected=expected)
            def save(script):
                current=rules();row=dict(current['rows'][0]['rule']) if current['rows'] else dict(Id='',Code='A4',Name='Float',Level='一般',Enabled=True,ParameterId='工仪全程/A4',SourceIP='127.0.0.1')
                row['Script']=script;c.call('/api/continuous-rules/save',dict(rule=row,revision=current['revision']))
            def body(name,value,kind='number'):
                data=info();row=next(r for r in data['rows'] if r['name']==name);return dict(name=name,kind=kind,value=value,revision=row['revision'],generation=data['generation'])
            def put(name,value,kind='number',expected=200):return c.call('/api/continuous-rules/globals',body(name,value,kind),expected=expected)
            def send():
                nonlocal seq
                seq+=1;sender.sendto(packet(seq),('127.0.0.1',udp));wait(lambda:c.call('/api/state')['accepted']>=seq,'telemetry')
            save('if(gzMode==1) then RET=Normal; else RET=Error; end')
            check(set(variables())=={'gzMode'} and not info()['canModify'] and not variables()['gzMode']['hasValue'],'variables are extracted from rule references and visible before listening')
            put('gzMode','1',expected=409)
            check(not variables()['gzMode']['hasValue'],'editing requires an active listener')
            adapter=next(a for a in c.call('/api/send-monitor/interfaces')['interfaces'] if a['loopback']);c.call('/api/send-monitor/config',dict(SourceIPs=['127.0.0.1'],DestinationIPs=['127.0.0.2'],InterfaceId=adapter['id'],DestinationPort=0));c.call('/api/control',dict(action='start'));send()
            c.call('/api/continuous-rules/globals',body('gzMode','1'),expected=403,csrf=False);control('start',400)
            check(not rules()['execution']['running'],'uninitialized manual-only input blocks judgment and edits require CSRF')
            put('gzMode','1');check(variables()['gzMode']['prepared'],'listening alone accepts a value for the next judgment start')
            control('start');wait(lambda:result()=='Normal','prepared value');check(variables()['gzMode']['valueText']=='1' and not variables()['gzMode']['prepared'],'manual input seeds the next run and read-only shared input can execute')
            stale=body('gzMode','2');received=c.call('/api/state')['accepted'];put('gzMode','0');wait(lambda:result()=='Error','manual dependency')
            check(c.call('/api/state')['accepted']==received and info()['running'],'live edit wakes dependent rules without a new UDP frame or stopping judgment')
            c.call('/api/continuous-rules/globals',stale,expected=409);check(variables()['gzMode']['valueText']=='0','stale multiuser edit cannot overwrite a newer value')
            put('gzMode','true','boolean');check(variables()['gzMode']['kind']=='boolean' and variables()['gzMode']['valueText']=='true','boolean state is stored as a boolean')
            put('gzMode','Normal','result');check(variables()['gzMode']['kind']=='result' and variables()['gzMode']['valueText']=='Normal','symbolic judgment state uses the existing expression constants')
            precise='0.1234567890123456789012345';put('gzMode',precise);check(variables()['gzMode']['valueText']==precise,'numeric text preserves decimal precision')
            put('gzMode','','nil');check(variables()['gzMode']['hasValue'] and variables()['gzMode']['valueText']=='NIL','explicit NIL remains distinct from never assigned')
            for value,kind in [('NaN','number'),('1','boolean'),('x','text')]:put('gzMode',value,kind,expected=400)
            check(variables()['gzMode']['valueText']=='NIL','invalid types and values leave the shared state unchanged')
            control('stop');save('InitRegion gzSelf=0; EndInitRegion ResultRegion if(gzSelf==1) then RET=Normal; else RET=Error; end EndResultRegion');control('start');wait(lambda:result()=='Error','self reader')
            put('gzSelf','1');wait(lambda:result()=='Normal','self writer re-evaluation');check(variables()['gzSelf']['actor'].startswith('人工：'),'manual changes also wake the same rule that writes the variable')
            control('stop');save('gzSelf=0; if(gzSelf==0) then RET=Normal; else RET=Error; end');control('start');put('gzSelf','1')
            check(variables()['gzSelf']['valueText']=='0' and variables()['gzSelf']['actor']=='规则赋值','later rule assignment may overwrite an artificial value')
            control('stop');save('if(HOLD(gzMode==1,0.15s)) then RET=Normal; else RET=Unusual; end');put('gzMode','1');control('start');wait(lambda:result()=='Normal','hold matures');put('gzMode','0');wait(lambda:result()=='Unusual','hold resets')
            check(info()['running'],'shared input changes reset continuous time conditions while running')
            control('stop');save('if(FRAMES(gzMode==1,2,this)) then RET=Normal; else RET=Unusual; end');put('gzMode','1');control('start');send();put('gzMode','1');check(result()=='Unusual','manual change never counts as a new telemetry frame');send();wait(lambda:result()=='Normal','frame count');put('gzMode','0');wait(lambda:result()=='Unusual','frame reset')
            check(True,'manual shared-state changes reset frame conditions without adding frames')
            old=body('gzMode','1');c.call('/api/control',dict(action='stop'));check(not info()['canModify'],'stopped reception disables editing');put('gzMode','1',expected=409)
            c.call('/api/control',dict(action='start'));check(not variables()['gzMode']['hasValue'],'new listener clears runtime shared values');c.call('/api/continuous-rules/globals',old,expected=409)
            check(not rules()['execution']['running'],'a previous listener session cannot write into a new session')
            check(any('共享变量人工修改' in line for line in (root/'logs').glob('*.jsonl') for line in line.read_text(encoding='utf-8').splitlines()),'manual state changes are recorded in the operation log')
        finally:
            sender.close()
            if process.poll() is None:process.terminate();process.wait(timeout=6)
    print('All',len(checks),'shared-variable checks passed.')

if __name__=='__main__':main()
