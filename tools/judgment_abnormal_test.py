"""Persistent judgment incidents, mandatory individual releases and six-column CSV."""
import csv,io,json,secrets,shutil,socket,struct,subprocess,sys,tempfile,time
from pathlib import Path
from integration_test import ROOT,Client,free_port,packet,seal,seed_send_monitor,wait

def main():
    executable=Path(sys.argv[1]).resolve() if len(sys.argv)>1 else ROOT/'TelemetryMonitor.exe'
    checks=[]
    def check(ok,label):assert ok,label;checks.append(label);print('PASS',label,flush=True)
    with tempfile.TemporaryDirectory(prefix='judgment-abnormal-') as temporary:
        root=Path(temporary);shutil.copytree(ROOT/'config',root/'config');shutil.copytree(ROOT/'web',root/'web');seed_send_monitor(root)
        for name in ['decoder.json','continuous-rules.json','tc-bindings.json']:(root/'config'/name).unlink(missing_ok=True)
        port=free_port();udp=free_port(socket.SOCK_DGRAM);settings=json.loads((root/'config/server.json').read_text(encoding='utf-8-sig'));settings.update(BindAddress='127.0.0.1',UdpPort=udp,SourceIPs=['127.0.0.1'],MappingConfirmed=True,DataTimeoutMs=10000,MulticastGroup='',MulticastInterface='');(root/'config/server.json').write_text(json.dumps(settings),encoding='utf-8')
        def start():return subprocess.Popen([str(executable),'--local','--root',str(root),'--web-port',str(port)],stdout=subprocess.DEVNULL,stderr=subprocess.STDOUT)
        process=start();sender=socket.socket(socket.AF_INET,socket.SOCK_DGRAM);sender.bind(('127.0.0.1',0));seq=0;c=Client(port);password=secrets.token_urlsafe(18)
        try:
            def ready():
                try:c.call('/api/auth/me');return True
                except OSError:return False
            wait(ready,'history server');c.call('/api/continuous-rules/abnormals',expected=401);c.call('/api/auth/setup',dict(username='incidentqa',name='异常记录测试',password=password));c.csrf=c.call('/api/auth/login',dict(username='incidentqa',password=password))['csrf']
            def state():return c.call('/api/continuous-rules')
            def history(query=''):return c.call('/api/continuous-rules/abnormals'+query)
            def control(action):return c.call('/api/continuous-rules/control',dict(action=action))
            def save(script,name='参数,"名称"\n完整保留'):
                s=state();rule=dict(s['rows'][0]['rule']) if s['rows'] else dict(Id='',Code='A4',Name=name,Level='重要',Enabled=True,ParameterId='工仪全程/A4',SourceIP='127.0.0.1')
                rule.update(Script=script,Name=name);c.call('/api/continuous-rules/save',dict(rule=rule,revision=s['revision']))
            def send(value):
                nonlocal seq
                seq+=1;data=packet(seq);struct.pack_into('>f',data,35,value);seal(data);sender.sendto(data,('127.0.0.1',udp));wait(lambda:c.call('/api/state')['accepted']>=seq,'history UDP')
            def release(id,reason,expected=200,csrf=True):return c.call('/api/continuous-rules/abnormals/release',dict(id=id,reason=reason),expected=expected,csrf=csrf)
            original='// 原规则,"保留"\nVAL=this.value*2;\nRANGE=[0 1];';save(original)
            adapter=next(a for a in c.call('/api/send-monitor/interfaces')['interfaces'] if a['loopback']);c.call('/api/send-monitor/config',dict(SourceIPs=['127.0.0.1'],DestinationIPs=['127.0.0.2'],InterfaceId=adapter['id'],DestinationPort=0));c.call('/api/control',dict(action='start'));control('start')
            check(history()['total']==0,'waiting for data creates no judgment incident')
            send(2);check(history()['total']==1 and history()['pending']==1 and history()['records'][0]['value']=='4' and history()['records'][0]['engineeringValue']=='2','abnormal stores actual VAL target plus original engineering value')
            unified=c.call('/api/abnormal-records');check(unified['total']==1 and unified['records'][0]['type']=='continuous' and unified['records'][0]['value']=='4','live unified view merges the general alarm and retains transformed judgment value')
            send(3);check(history()['total']==1,'consecutive abnormal frames form one incident')
            send(0);check(history()['pending']==1 and not history()['records'][0]['released'],'automatic recovery never releases the stored incident')
            send(-3);check(history()['total']==2 and history()['pending']==2 and history()['records'][0]['value']=='-6','recurrence creates a separate individually pending record')
            check(sum(r['category']=='常态化判读' for r in c.call('/api/alarms/list')['records'])==1,'changed abnormal engineering values and recovery preserve one general warning while independent incidents remain complete')
            for reason in ['', ' \r\n ', 'x'*2001]:release(1,reason,400)
            release(1,'未授权',403,False);release(999,'不存在',400)
            check(history()['pending']==2,'reason is mandatory and release requires an authenticated CSRF-protected request')
            reason='已核实,"试验注入"\n人工解除';release(1,reason);release(1,'覆盖原因',409)
            old=next(r for r in history()['records'] if r['id']==1)
            check(history()['pending']==1 and old['reason']==reason and old['releasedBy']=='异常记录测试' and old['releasedAt'],'individual release records reason, actor and time and cannot overwrite an earlier release')
            release(2,'已核实');send(-3);check(history()['total']==2 and history()['pending']==0 and state()['rows'][0]['result']=='Error','release changes history status only and persistent abnormality is not re-created every frame')
            send(0);send(4);c.call('/api/control',dict(action='ack'));check(history()['total']==3 and history()['pending']==1,'acknowledging general reception alarms does not release judgment incidents')
            control('stop');new_script='VAL=this.value*3; RANGE=[0 1];';save(new_script,'修改后名称');control('start')
            check(history()['total']==4 and history()['records'][0]['script']==new_script and next(r for r in history()['records'] if r['id']==1)['script']==original,'new runs create their own incident and prior script/name/value snapshots remain immutable')
            check(sum(r['category']=='常态化判读' for r in c.call('/api/alarms/list')['records'])==1 and c.call('/api/alarms/list')['pending']==0,'restarting judgment within the same listener does not re-arm an acknowledged general warning')
            control('stop');save('VAL=1/0; RET=Normal;');control('start');send(4)
            check(history()['total']==5 and history()['records'][0]['result']=='执行异常' and history()['records'][0]['detail'],'execution faults are stored once with their explanation')
            control('stop');save('if(HOLD(this.value>0,0.1s)) then RET=Error; else RET=Normal; end');control('start');wait(lambda:history()['total']==6,'timed incident');time.sleep(0.18)
            check(history()['total']==6,'timer dispatches do not flood duplicate persistent incidents')
            page=history('?page=1&pageSize=2&status=all');older=history('?page=2&pageSize=2&throughId='+str(page['throughId']))
            check(len(page['records'])==2 and len(older['records'])==2 and page['records'][0]['id']>older['records'][0]['id'],'history pages newest first using a stable record cursor')
            check(history('?status=released')['matching']==2 and history('?status=pending')['matching']==4,'released and pending records are independently filterable')
            with c.opener.open(c.base+'/api/continuous-rules/abnormals/export.csv',timeout=10) as response:raw=response.read()
            exported=list(csv.reader(io.StringIO(raw.decode('utf-8-sig'),newline='')))
            check(raw.startswith(b'\xef\xbb\xbf') and exported[0]==['参数代号','参数名称','异常值','是否解除','解除原因','判读规则'] and len(exported)==7,'UTF-8 BOM CSV exports all history in the exact requested six columns')
            check(exported[1]==['A4','参数,"名称"\n完整保留','4','是',reason,original] and exported[2][2]=='-6','CSV retains multiline reason/rule, quotes, commas and negative numeric abnormal values')
            before=history();control('stop');c.call('/api/control',dict(action='stop'));process.terminate();process.wait(timeout=6)
            journal=root/'data/judgment-abnormal-history.jsonl';journal.write_bytes(journal.read_bytes()+b'{"Kind":"abnor')
            process=start();c=Client(port);wait(ready,'history restart');c.csrf=c.call('/api/auth/login',dict(username='incidentqa',password=password))['csrf']
            check(history()['records']==before['records'] and history()['pending']==before['pending'] and list((root/'data').glob('judgment-abnormal-history.jsonl.recovered-*.bak')),'reboot preserves incidents/releases and recovers an interrupted trailing write with backup')
            check(not state()['execution']['running'],'history restoration never starts judgment automatically')
            process.terminate();process.wait(timeout=6);damaged=journal.read_bytes()+b'not-json\n';journal.write_bytes(damaged)
            process=start();c=Client(port);wait(ready,'damaged history server');c.csrf=c.call('/api/auth/login',dict(username='incidentqa',password=password))['csrf']
            c.call('/api/control',dict(action='start'));c.call('/api/continuous-rules/control',dict(action='start'),expected=409)
            check(history()['storageError'] and journal.read_bytes()==damaged and not state()['execution']['running'],'damaged history stays intact and blocks judgment rather than silently dropping saved incidents')
        finally:
            sender.close()
            if process.poll() is None:process.terminate();process.wait(timeout=6)
    print('All',len(checks),'judgment abnormal-history checks passed.')

if __name__=='__main__':main()
