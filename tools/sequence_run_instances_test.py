"""Named run instances: isolated storage, real loopback UDP, never physical device destinations."""
import csv,io,json,secrets,shutil,socket,subprocess,sys,tempfile,time
from pathlib import Path
from urllib.parse import urlencode
from integration_test import ROOT,Client,free_port,wait,packet,seal,seed_send_monitor


def main():
    exe=Path(sys.argv[1]).resolve();checks=[]
    def check(ok,label):
        assert ok,label
        checks.append(label);print('PASS',label,flush=True)
    with tempfile.TemporaryDirectory(prefix='sequence-instances-',dir=ROOT/'test-output') as scratch:
        root=Path(scratch);shutil.copytree(ROOT/'config',root/'config');shutil.copytree(ROOT/'web',root/'web')
        for name in ['decoder.json','continuous-rules.json','tc-bindings.json','command-send.json','sequences.json','observed-commands.json']:(root/'config'/name).unlink(missing_ok=True)
        def save(name,value):(root/'config'/name).write_text(json.dumps(value),encoding='utf-8')
        seed_send_monitor(root);udp=free_port(socket.SOCK_DGRAM);web=free_port();source=free_port(socket.SOCK_DGRAM);c=Client(web)
        settings=json.loads((root/'config/server.json').read_text(encoding='utf-8-sig'));settings.update(BindAddress='127.0.0.1',UdpPort=udp,SourceIPs=['127.0.0.1'],MappingConfirmed=True,MulticastGroup='',MulticastInterface='',DataTimeoutMs=60000);save('server.json',settings)
        save('observed-commands.json',dict(SchemaVersion=1,SourceIP='127.0.0.1',NextNumber=3,Commands=[dict(Number=i,Name='loopback '+str(i),DestinationIP='127.0.0.2',DestinationIPs=['127.0.0.2'],PayloadHex=f'DE AD 73 {i:02X}') for i in [1,2]]))
        save('tc-bindings.json',dict(SchemaVersion=1,Revision='instances',Bindings=[dict(Code=f'K00{i}',Number=i) for i in [1,2]]))
        receiver=socket.socket(socket.AF_INET,socket.SOCK_DGRAM);receiver.bind(('127.0.0.2',0));receiver.settimeout(3)
        save('command-send.json',dict(SourceIP='127.0.0.1',Routes=[dict(DestinationIP='127.0.0.2',SourcePort=source,DestinationPort=receiver.getsockname()[1])]))
        def row(keyword='',condition='',command='',expected='',mark='',timeout='15'):
            return dict(Keyword=keyword,Description='instance step',Condition=condition,Command=command,Expected=expected,Mark=mark,Timeout='' if keyword in ['TEXT','PAUSE'] else timeout)
        definitions=[]
        def definition(mode,rows):
            n=len(definitions)+1;d=dict(Id=f'{n:032x}',Code=('XLZ' if mode=='execute' else 'XLP')+f'{n:04}',Name='instance-'+str(n),Mode=mode,SourceIP='127.0.0.1',Rows=[dict(Row=i+2,**r) for i,r in enumerate(rows)]);definitions.append(d);return d
        j=definition('judge',[row('TEXT',mark='T0'),row(condition='abs(TM.A1.value-9.8)<1')])
        p=definition('execute',[row('TEXT',mark='T0'),row('PAUSE')])
        call=definition('execute',[row('CALL',command=j['Code'],expected='.2'),row('PAUSE')])
        jump=definition('execute',[row('CALL',command=p['Code'],expected='.2')])
        send=definition('execute',[row(command='K001',expected='.2'),row(command='K002',expected='.2')])
        monitor=definition('judge',[row(command='K001')])
        instant=definition('judge',[row('TEXT',mark='T0')])
        timeout=definition('judge',[row(condition='TM.A1.value==777',timeout='.25')])
        save('sequences.json',dict(SchemaVersion=1,Revision='instances',DefaultTimeoutSeconds=15,NextJudgeNumber=20,NextExecuteNumber=20,Sequences=definitions))
        proc=None;sender=socket.socket(socket.AF_INET,socket.SOCK_DGRAM);frame=0
        def launch():return subprocess.Popen([str(exe),'--local','--root',str(root),'--web-port',str(web)],stdout=subprocess.DEVNULL,stderr=subprocess.STDOUT,creationflags=subprocess.CREATE_NO_WINDOW)
        def ready():
            try:c.call('/api/auth/me');return True
            except OSError:return False
        def cfg():return c.call('/api/sequence/config')
        def tasks():return c.call('/api/sequence/tasks')['rows']
        def state(run):return c.call('/api/sequence/state?'+urlencode(dict(runId=run,page=1)))
        def begin(d,name,request=None):
            body=dict(action='start',id=d['Id'],runName=name,revision=cfg()['revision'],requestId=request or secrets.token_hex(16))
            return c.call('/api/sequence/control',body),body
        def stop(run):c.call('/api/sequence/control',dict(action='stop',runId=run))
        def data(value):
            nonlocal frame
            frame+=1;raw=packet(frame);raw[22:27]=round(value*10000).to_bytes(5,'big');sender.sendto(seal(raw),('127.0.0.1',udp))
        try:
            proc=launch();wait(ready,'server');password=secrets.token_urlsafe(18);c.call('/api/auth/setup',dict(username='instanceqa',name='运行实例测试',password=password));c.csrf=c.call('/api/auth/login',dict(username='instanceqa',password=password))['csrf'];c.call('/api/control',dict(action='start'))
            a,first_request=begin(j,'同名任务');b,_=begin(j,'同名任务');a,b=a['runId'],b['runId']
            check(a!=b and state(a)['runtime']['running'] and state(b)['runtime']['running'],'same definition and same name create independent simultaneous runs')
            stop(a);check(not state(a)['runtime']['running'] and state(b)['runtime']['running'],'stop routes to exact run ID instead of latest mode or definition')
            frozen=state(a);data(9.79);wait(lambda:not state(b)['runtime']['running'],'second judge completes')
            check(state(b)['rows'][1]['status']=='通过' and state(a)['rows']==frozen['rows'],'new telemetry completes only eligible run and cannot rewrite ended run results')
            retry=c.call('/api/sequence/control',first_request);check(retry['runId']==a and not retry['running'],'retrying an earlier start request returns its original run after later launches')
            data(0);wait(lambda:c.call('/api/telemetry/history?reference=TM.A1.value&sourceIP=127.0.0.1')['value']==0,'nonmatching current value before pause isolation')
            e1,_=begin(p,'执行甲');e2,_=begin(p,'执行乙');j1,_=begin(j,'判读甲');e1,e2,j1=e1['runId'],e2['runId'],j1['runId']
            check(state(e1)['runtime']['paused'] and state(e2)['runtime']['paused'] and state(j1)['runtime']['running'],'multiple executions and judgments retain separate pause states')
            before1=state(e1)['runtime'];before2=state(e2)['runtime'];beforej=state(j1)['runtime'];time.sleep(.15)
            check(abs(state(e1)['runtime']['elapsed']-before1['elapsed'])<.01 and abs(state(e2)['runtime']['elapsed']-before2['elapsed'])<.01 and state(j1)['runtime']['elapsed']>beforej['elapsed']+.1,'each frozen clock is independent while other judgment keeps timing')
            c.call('/api/sequence/control',dict(action='resume',runId=e1,controlRevision=before1['controlRevision']))
            check(not state(e1)['runtime']['running'] and state(e2)['runtime']['paused'],'resume acts on one selected execution task')
            x,_=begin(call,'CALL执行甲');y,_=begin(call,'CALL执行乙');x,y=x['runId'],y['runId'];wait(lambda:state(x)['runtime']['paused'] and state(y)['runtime']['paused'],'both CALL parents continue to pause')
            children=[r for r in tasks() if r['parentRunId'] in [x,y]]
            check(len(children)==2 and len({r['runId'] for r in children})==2 and all(r['name']==j['Name'] and r['running'] for r in children),'CALL creates distinct named judgments even when target definition is already running')
            check(state(x)['runtime']['paused'] and state(y)['runtime']['paused'] and state(j1)['runtime']['running'],'CALL leaves both parent executions and preexisting judgment unchanged')
            data(9.79);wait(lambda:all(not state(r['runId'])['runtime']['running'] for r in children) and not state(j1)['runtime']['running'],'one telemetry frame serves all independent waiting judgments')
            check(all(state(r['runId'])['runtime']['abnormal']==0 for r in children),'eligible new telemetry fans out to every independent judgment')
            z,_=begin(jump,'末尾跳转源');z=z['runId'];wait(lambda:any(r['parentRunId']==z for r in tasks()),'same-mode CALL target')
            target=next(r for r in tasks() if r['parentRunId']==z)
            check(not state(z)['runtime']['running'] and target['paused'] and target['runId']!=z,'same-mode jump preserves ended source and creates fresh target instance')
            check(target['initialF0']==0 and target['initialT0'] is None and state(z)['runtime']['name']=='末尾跳转源','CALL target has independent clocks and source name is retained')
            for r in tasks():
                if r['running']:stop(r['runId'])
            watchers=[begin(monitor,'指令监视'+str(i))[0]['runId'] for i in range(3)]
            send_runs=[begin(send,'发送任务'+str(i))[0]['runId'] for i in range(3)]
            received=[receiver.recvfrom(65535) for _ in range(6)]
            wait(lambda:all(not state(r)['runtime']['running'] for r in send_runs+watchers),'all senders and command monitors finish')
            check(sum(v[0]==bytes.fromhex('DE AD 73 01') for v in received)==3 and sum(v[0]==bytes.fromhex('DE AD 73 02') for v in received)==3 and all(v[1][1]==source for v in received),'shared fair dispatch sends each command once with correct source port and loses no pending work')
            check(all(state(r)['runtime']['abnormal']==0 and all(row['status']=='通过' for row in state(r)['rows']) for r in send_runs+watchers),'multiple send callbacks and monitored events update only their own eligible task instances')
            later,_=begin(monitor,'后来启动的指令监视');later=later['runId'];time.sleep(.15);check(state(later)['runtime']['running'] and state(later)['rows'][0]['status']=='等待指令','previous command events cannot satisfy a later run');stop(later)
            for run in send_runs:
                hist=c.call('/api/sequence/history?runId='+run)['rows'];check(sum(r['Event']=='指令发送' for r in hist)==2 and all(r['RunId']==run for r in hist),'history and command audit remain scoped to '+run[:8])
            data(0);wait(lambda:c.call('/api/telemetry/history?reference=TM.A1.value&sourceIP=127.0.0.1')['value']==0,'nonmatching current value before timeout isolation')
            t,_=begin(timeout,'独立超时');live,_=begin(j,'超时旁路');t,live=t['runId'],live['runId'];wait(lambda:not state(t)['runtime']['running'],'timeout task')
            check(state(t)['runtime']['abnormal']==1 and state(live)['runtime']['running'],'one task timeout cannot terminate a different judgment');stop(live)
            archive_before=state(a)['rows']
            for i in range(14):begin(instant,'完成任务'+str(i))
            check(state(a)['rows']==archive_before and state(a)['runtime']['name']=='同名任务','evicted old task details reload exactly from its own immutable archive')
            diag=c.call('/api/sequence/diagnostic?runId='+a);check(diag['runtime']['runId']==a,'diagnostic export resolves an older run ID after detail cache eviction')
            unrelated=c.call('/api/sequence/diagnostic?id='+p['Id'])
            check(unrelated.get('runtimeMatchesSelection') is False and unrelated['definition']['Id']==p['Id'] and not unrelated['rows'],
                  'requesting a different definition cannot return cached diagnostics from the archived last execution')
            archive_path=next((root/'data/archives/sequences').rglob(a+'.jsonl'));archive=archive_path.read_bytes();check(archive and all(json.loads(line)['RunId']==a for line in archive.splitlines()),'each independent task has its own durable archive')
            first_data=state(b)['rows'][1]['values'];check(first_data and state(b)['rows'][1]['status']=='通过','successful engineering values survive task cache eviction')
            live_runs=[begin(j,'全停判读'+str(i))[0]['runId'] for i in range(2)]+[begin(p,'全停执行'+str(i))[0]['runId'] for i in range(2)]
            c.call('/api/control',dict(action='stop'));check(all(not state(r)['runtime']['running'] for r in live_runs),'stopping shared reception stops every dependent run including multiple runs per mode')
            events=[json.loads(line) for line in (root/'data/sequence-history.jsonl').read_text(encoding='utf-8').splitlines()]
            starts=[e['RunId'] for e in events if e['Event']=='开始'];ends=[e['RunId'] for e in events if e['Event']=='结束']
            check(len(starts)==len(set(starts))==len(ends)==len(set(ends)) and set(starts)==set(ends),'every launched instance has exactly one persisted start and one finish')
            exported=list(csv.reader(io.StringIO(c.opener.open(c.base+'/api/sequence/export.csv?runId='+b).read().decode('utf-8-sig'))));check(all(len(row)==11 for row in exported) and all(row[1]=='同名任务' for row in exported[1:]),'per-run CSV retains requested eleven-column format')
            session=c.call('/api/sequence/tasks')['session'];proc.terminate();proc.wait(timeout=8);proc=launch();wait(ready,'restart');c.csrf=c.call('/api/auth/login',dict(username='instanceqa',password=password))['csrf']
            current=c.call('/api/sequence/tasks');check(current['session']!=session and current['rows']==[] and not cfg()['error'],'server restart preserves archive consistency and never resumes or resends old tasks')
            check(archive_path.read_bytes()==archive,'restart does not rewrite completed task archives')
            report=ROOT/'test-output/sequence-run-instances/integration-report.json';report.write_text(json.dumps(dict(checks=len(checks),details=checks),ensure_ascii=False,indent=2),encoding='utf-8');print('All',len(checks),'run instance checks passed.')
        finally:
            sender.close();receiver.close()
            if proc:proc.terminate();proc.wait(timeout=8)

if __name__=='__main__':main()
