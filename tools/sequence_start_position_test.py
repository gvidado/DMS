"""Partial sequence starts and preset clocks using isolated storage and loopback UDP only."""
import csv, io, json, secrets, shutil, socket, subprocess, sys, tempfile, time
from pathlib import Path
from integration_test import ROOT, Client, free_port, wait, packet, seal, seed_send_monitor


def main():
    exe=Path(sys.argv[1]).resolve();checks=[]
    def check(ok,label):assert ok,label;checks.append(label);print('PASS',label,flush=True)
    with tempfile.TemporaryDirectory(prefix='sequence-entry-',dir=ROOT/'test-output') as scratch:
        root=Path(scratch);shutil.copytree(ROOT/'config',root/'config');shutil.copytree(ROOT/'web',root/'web')
        for name in ['decoder.json','continuous-rules.json','tc-bindings.json','sequences.json','observed-commands.json','command-send.json']:(root/'config'/name).unlink(missing_ok=True)
        def save(name,value):(root/'config'/name).write_text(json.dumps(value),encoding='utf-8')
        seed_send_monitor(root);udp=free_port(socket.SOCK_DGRAM);web=free_port();c=Client(web)
        settings=json.loads((root/'config/server.json').read_text(encoding='utf-8-sig'));settings.update(BindAddress='127.0.0.1',UdpPort=udp,SourceIPs=['127.0.0.1'],MappingConfirmed=True,MulticastGroup='',MulticastInterface='',DataTimeoutMs=60000);save('server.json',settings)
        save('observed-commands.json',dict(SchemaVersion=1,SourceIP='127.0.0.1',NextNumber=4,Commands=[dict(Number=i,Name='loopback '+str(i),DestinationIP='127.0.0.2',DestinationIPs=['127.0.0.2'],PayloadHex=f'AA BB {i:02X}') for i in [1,2,3]]))
        save('tc-bindings.json',dict(SchemaVersion=1,Revision='entry',Bindings=[dict(Code=f'K00{i}',Number=i) for i in [1,2,3]]))
        receiver=socket.socket(socket.AF_INET,socket.SOCK_DGRAM);receiver.bind(('127.0.0.2',0));receiver.settimeout(3)
        save('command-send.json',dict(SourceIP='127.0.0.1',Routes=[dict(DestinationIP='127.0.0.2',SourcePort=free_port(socket.SOCK_DGRAM),DestinationPort=receiver.getsockname()[1])]))
        def row(n,keyword='',command='',condition='',expected='',mark='',timeout='3'):
            return dict(Row=n,Keyword=keyword,Description='entry step',Command=command,Condition=condition,Mark=mark,Expected=expected,Timeout='' if keyword in ['TEXT','PAUSE'] else timeout)
        definitions=[]
        def definition(mode,rows):
            n=len(definitions)+1;d=dict(Id=f'{n:032x}',Code=('XLZ' if mode=='execute' else 'XLP')+f'{n:04}',Name=f'entry-{mode}-{n}',Mode=mode,SourceIP='127.0.0.1',Rows=rows);definitions.append(d);return d
        execute=definition('execute',[row(2,'TEXT',mark='T0'),row(4,command='K001',expected='.2'),row(8,command='K002',expected='F:20.4'),row(10,command='K003',expected='T0:10.9'),row(12,condition='TM.A1.value==5',expected='.2'),row(14,'PAUSE'),row(16,condition='TM.A1.value==6',expected='F:21.4')])
        judge=definition('judge',[row(2,'TEXT',mark='T0'),row(5,condition='TM.A1.value==1',expected='T0:10.5'),row(9,condition='TM.A1.value==2',expected='F:20.9')])
        parallel=definition('judge',[row(2,'TEXT'),row(4,'PAR'),row(6,condition='TM.A1.value==999'),row(8,'HOLD',condition='TM.A2.value==0'),row(10,condition='TM.A1.value==7'),row(12,'CHANGE'),row(14,condition='TM.A1.value==8')])
        reset=definition('judge',[row(2,'TEXT',mark='T0'),row(4,condition='TM.A1.value==3',expected='T0:.3')])
        target=definition('judge',[row(2,condition='TM.A1.value==99')])
        call=definition('execute',[row(2,'TEXT',mark='T0'),row(4,'CALL',command=target['Code'],expected='F:20.2'),row(6,'PAUSE')])
        next_execute=definition('execute',[row(2,'PAUSE')])
        jump=definition('execute',[row(2,'TEXT'),row(4,'CALL',command=next_execute['Code'],expected='F:20.2')])
        save('sequences.json',dict(SchemaVersion=1,Revision='entry',DefaultTimeoutSeconds=3,NextJudgeNumber=20,NextExecuteNumber=20,Sequences=definitions))
        proc=subprocess.Popen([str(exe),'--local','--root',str(root),'--web-port',str(web)],stdout=subprocess.DEVNULL,stderr=subprocess.STDOUT,creationflags=subprocess.CREATE_NO_WINDOW)
        sender=socket.socket(socket.AF_INET,socket.SOCK_DGRAM);sender.bind(('127.0.0.1',0));seq=0;last_start=None
        def ready():
            try:c.call('/api/auth/me');return True
            except OSError:return False
        def cfg():return c.call('/api/sequence/config')
        def state(d):return c.call('/api/sequence/state?id='+d['Id'])
        def start(d,expected=200,**options):
            nonlocal last_start
            last_start=dict(action='start',id=d['Id'],mode=d['Mode'],runName=d['Name'],revision=cfg()['revision'],requestId=secrets.token_hex(16),**options)
            return c.call('/api/sequence/control',last_start,expected=expected)
        def stop(d):
            r=state(d)['runtime']
            if r['running']:c.call('/api/sequence/control',dict(action='stop',runId=r['runId']))
        def row_state(d,n):return next(r for r in state(d)['rows'] if r['input']['Row']==n)
        def waiting(d,n):wait(lambda:row_state(d,n)['status']=='等待参数',f'row {n} ready')
        def done(d):wait(lambda:not state(d)['runtime']['running'],'sequence finishes')
        def data(value):
            nonlocal seq
            seq+=1;raw=packet(seq);raw[22:27]=round(value*10000).to_bytes(5,'big');raw[27:30]=bytes(3);sender.sendto(seal(raw),('127.0.0.1',udp));wait(lambda:(c.call('/api/telemetry/history?reference=TM.A1&sourceIP=127.0.0.1')['current'] or {}).get('sequence')==seq,'telemetry arrival')
        try:
            wait(ready,'entry server');password=secrets.token_urlsafe(18);c.call('/api/auth/setup',dict(username='entryqa',name='Entry QA',password=password));c.csrf=c.call('/api/auth/login',dict(username='entryqa',password=password))['csrf'];c.call('/api/control',dict(action='start'))
            check('原表' in start(judge,expected=400,startRow=6)['error'],'nonexistent original row is rejected before a run starts')
            check('T0' in start(judge,expected=400,startRow=5)['error'],'skipping the T0 marker requires an explicit initial T0')
            for field,value in [('startRow','-1'),('startRow','2.5'),('t0Elapsed','-1'),('t0Elapsed','NaN'),('f0Elapsed','Infinity'),('f0Elapsed','604801'),('t0Elapsed','F0=10')]:
                check('error' in start(judge,expected=400,**{field:value}),'invalid startup value rejected: '+field+'='+value)
            check(not cfg()['runtimes']['judge']['running'],'invalid requests never start the sequence')
            data(5);before=time.monotonic();started=start(execute,startRow=8,t0Elapsed='T0=10',f0Elapsed='F0=20');run=started['runId']
            check(started['skipped']==2 and started['startRow']==8 and started['t0Preset'] and started['t0']==10 and started['elapsed']>=20,'selected original row and both elapsed-clock presets are applied')
            first,_=receiver.recvfrom(65535);at_first=time.monotonic();second,_=receiver.recvfrom(65535);at_second=time.monotonic()
            check(first==bytes.fromhex('AA BB 02') and second==bytes.fromhex('AA BB 03') and at_first-before>=.35 and at_second-at_first>=.4,'skipped command is never sent; F and T0 schedules wait only their remaining time')
            waiting(execute,12);check(row_state(execute,12)['status']=='等待参数','old matching telemetry is not replayed as a new pass')
            data(5);wait(lambda:state(execute)['runtime']['paused'],'execution pauses')
            frozen=state(execute)['runtime'];time.sleep(.15);later=state(execute)['runtime']
            check(abs(later['elapsed']-frozen['elapsed'])<.01 and abs(later['t0Elapsed']-frozen['t0Elapsed'])<.01,'pause freezes both preset clocks')
            duplicate=c.call('/api/sequence/control',last_start);check(duplicate['runId']==run and duplicate['paused'],'retrying one start request does not restart or resend')
            c.call('/api/sequence/control',dict(action='resume',runId=run,controlRevision=later['controlRevision']));waiting(execute,16);data(6);done(execute)
            rows=state(execute)['rows'];check(all(r['status']=='已跳过' and r['actual'] is None for r in rows[:2]) and '已跳过2项' in state(execute)['runtime']['reason'],'skipped rows retain explicit status with no fabricated execution time')
            hist=c.call('/api/sequence/history?runId='+run)['rows'];begin=next(e for e in hist if e['Event']=='开始')
            check(begin['Start']==dict(StartRow=8,F0=20,T0=10) and 'F0=20s' in begin['Reason'] and len([e for e in hist if e['Status']=='已跳过'])==2,'history persists initial clocks, entry row and all skipped steps')
            exported=list(csv.DictReader(io.StringIO(c.opener.open(c.base+'/api/sequence/export.csv?runId='+run).read().decode('utf-8-sig'))));check(any(e['事件']=='开始' and 'T0=10s' in e['异常原因'] for e in exported) and sum(e['状态']=='已跳过' for e in exported)==2,'CSV includes startup settings and explicit skipped results')
            start(judge,startRow=5,t0Elapsed=10,f0Elapsed=20);data(1);check(row_state(judge,5)['actual'] is None,'judgment waits until the preset T0 schedule before evaluating')
            waiting(judge,5);data(1);waiting(judge,9);data(2);done(judge)
            check(row_state(judge,5)['planned']==20.5 and row_state(judge,9)['planned']==20.9,'judgment honors both adjusted absolute clocks')
            start(judge,startRow=5,t0Elapsed='10');waiting(judge,5);check(row_state(judge,5)['planned']==.5 and state(judge)['runtime']['initialF0']==0,'T0 alone schedules remaining time correctly while F starts from zero');data(1);stop(judge)
            start(execute,startRow=16,f0Elapsed='F0=21');waiting(execute,16);check(row_state(execute,16)['planned']==21.4 and state(execute)['runtime']['t0'] is None,'F0 alone works without a T0 when no remaining step needs T0');data(6);done(execute)
            start(judge,startRow=5,t0Elapsed=20,f0Elapsed=30);waiting(judge,5);check(row_state(judge,5)['delay']>=9.4,'overdue preset schedule starts immediately and records lateness')
            data(1);waiting(judge,9);data(2);done(judge)
            start(parallel,startRow=8,f0Elapsed=5);data(7);waiting(parallel,14);data(8);done(parallel)
            check(all(row_state(parallel,n)['status']=='已跳过' for n in [2,4,6]) and all(row_state(parallel,n)['status']=='通过' for n in [8,10,12,14]),'entry within PAR runs only the remaining HOLD and parallel rows, then CHANGE and serial rows')
            start(parallel,startRow=12);waiting(parallel,14);data(8);done(parallel);check(state(parallel)['runtime']['skipped']==5,'entry at CHANGE bypasses the entire prior parallel block')
            start(reset,t0Elapsed=10,f0Elapsed=20);info=state(reset)['runtime'];check(not info['t0Preset'] and info['t0Elapsed']<.2 and info['t0']>=20,'an executed T0 marker replaces the startup preset')
            waiting(reset,4);data(3);done(reset)
            start(call,startRow=4,f0Elapsed=20,t0Elapsed=10);wait(lambda:state(call)['runtime']['paused'] and state(target)['runtime']['running'],'CALL launches judgment')
            info=state(target)['runtime'];check(info['initialF0']==0 and info['initialT0'] is None and info['startRow']==2 and state(call)['runtime']['initialF0']==20,'CALL judgment starts with its own clocks while execution retains its presets')
            stop(call);stop(target);start(jump,startRow=4,f0Elapsed=20);wait(lambda:state(next_execute)['runtime']['paused'],'same-mode CALL target');info=state(next_execute)['runtime'];check(info['initialF0']==0 and info['initialT0'] is None and info['startRow']==2,'same-mode terminal CALL also resets entry and clocks in the next run');stop(next_execute)
            start(target);info=state(target)['runtime'];check(info['startRow']==2 and info['initialF0']==0 and info['initialT0'] is None and info['skipped']==0,'leaving all startup options blank preserves normal first-row startup');stop(target)
            c.call('/api/control',dict(action='stop'));report=ROOT/'test-output/sequence-start-position/integration-report.json';report.write_text(json.dumps(dict(checks=len(checks),details=checks),ensure_ascii=False,indent=2),encoding='utf-8');print('All',len(checks),'sequence entry checks passed.')
        finally:sender.close();receiver.close();proc.terminate();proc.wait(timeout=8)


if __name__=='__main__':main()
