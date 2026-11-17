"""Independent sequence runtimes and CALL launches, using loopback UDP only."""
import csv,io,json,secrets,shutil,socket,subprocess,sys,tempfile,time,urllib.request
from pathlib import Path
from integration_test import ROOT,Client,free_port,wait,packet,seal,seed_send_monitor
from sequence_test import xlsx

def main():
    exe=Path(sys.argv[1]).resolve();checks=[]
    def check(ok,label):assert ok,label;checks.append(label);print('PASS',label,flush=True)
    with tempfile.TemporaryDirectory(prefix='independent-sequences-',dir=ROOT/'test-output') as scratch:
        root=Path(scratch);shutil.copytree(ROOT/'config',root/'config');shutil.copytree(ROOT/'web',root/'web')
        for name in ['decoder.json','continuous-rules.json','tc-bindings.json','command-send.json','sequences.json','observed-commands.json']:(root/'config'/name).unlink(missing_ok=True)
        seed_send_monitor(root);udp=free_port(socket.SOCK_DGRAM);source=free_port(socket.SOCK_DGRAM);web=free_port();c=Client(web)
        settings=json.loads((root/'config/server.json').read_text(encoding='utf-8-sig'));settings.update(BindAddress='127.0.0.1',UdpPort=udp,SourceIPs=['127.0.0.1'],MappingConfirmed=True,MulticastGroup='',MulticastInterface='',DataTimeoutMs=60000)
        def save(name,value):(root/'config'/name).write_text(json.dumps(value),encoding='utf-8')
        save('server.json',settings)
        save('observed-commands.json',dict(SchemaVersion=1,SourceIP='127.0.0.1',NextNumber=2,Commands=[dict(Number=1,Name='loopback',DestinationIP='127.0.0.2',DestinationIPs=['127.0.0.2'],PayloadHex='AA BB 01')]))
        save('tc-bindings.json',dict(SchemaVersion=1,Revision='test',Bindings=[dict(Code='K001',Number=1)]))
        receiver=socket.socket(socket.AF_INET,socket.SOCK_DGRAM);receiver.bind(('127.0.0.2',0));receiver.settimeout(2)
        save('command-send.json',dict(SourceIP='127.0.0.1',Routes=[dict(DestinationIP='127.0.0.2',SourcePort=source,DestinationPort=receiver.getsockname()[1])]))
        def row(keyword='',command='',condition='',mark='',expected='',timeout='10'):
            return dict(Keyword=keyword,Description='loopback step',Command=command,Condition=condition,Mark=mark,Expected=expected,Timeout='' if keyword=='TEXT' else timeout)
        definitions=[]
        def definition(n,mode,rows):
            d=dict(Id=f'{n:032x}',Code=('XLZ' if mode=='execute' else 'XLP')+f'{n:04}',Name=f'{mode}-{n}',Mode=mode,SourceIP='127.0.0.1',Rows=[dict(Row=i+2,**r) for i,r in enumerate(rows)]);definitions.append(d);return d
        j=definition(1,'judge',[row('TEXT',mark='T0'),row(condition='TM.A1.value==99',expected='T0:.1')])
        e=definition(2,'execute',[row('TEXT',mark='T0'),row('CALL',command=j['Code'],expected='.2'),row(command='K001',expected='.2'),row(condition='TM.A1.value==5',expected='.2')])
        jc=definition(3,'judge',[row(command='K001')])
        ec=definition(4,'execute',[row('CALL',command=jc['Code'],expected='.2'),row(command='K001',expected='.2')])
        et=definition(5,'execute',[dict(row('PAUSE'),Timeout='')])
        ej=definition(6,'execute',[row('CALL',command=et['Code'],expected='.2')])
        jt=definition(7,'judge',[row(condition='TM.A1.value==7',timeout='.2'),row(condition='TM.A1.value==8',timeout='.2')])
        ep=definition(8,'execute',[row('PAR',expected='.2'),row('CALL',command=j['Code'],expected='.2'),row(condition='TM.A1.value==5',expected='.2'),row('CHANGE',expected='.2')])
        save('sequences.json',dict(SchemaVersion=1,Revision='fixture',DefaultTimeoutSeconds=10,NextJudgeNumber=8,NextExecuteNumber=9,Sequences=definitions))
        proc=subprocess.Popen([str(exe),'--local','--root',str(root),'--web-port',str(web)],stdout=subprocess.DEVNULL,stderr=subprocess.STDOUT,creationflags=subprocess.CREATE_NO_WINDOW)
        sender=socket.socket(socket.AF_INET,socket.SOCK_DGRAM);seq=0
        def ready():
            try:c.call('/api/auth/me');return True
            except OSError:return False
        def cfg():return c.call('/api/sequence/config')
        def state(d):return c.call('/api/sequence/state?id='+d['Id'])
        def start(d,expected=200):return c.call('/api/sequence/control',dict(action='start',id=d['Id'],runName=d['Name'],requestId=secrets.token_hex(16),revision=cfg()['revision']),expected=expected)
        def stop(d):
            s=state(d)['runtime']
            if s['running']:c.call('/api/sequence/control',dict(action='stop',runId=s['runId']))
        def data(value):
            nonlocal seq
            seq+=1;raw=packet(seq);raw[22:27]=round(value*10000).to_bytes(5,'big');sender.sendto(seal(raw),('127.0.0.1',udp))
        def upload(rows,mode):
            url=c.base+f'/api/sequence/preview?filename=fixture.xlsx&mode={mode}&timeout=10&sourceIP=127.0.0.1'
            request=urllib.request.Request(url,data=xlsx([('check',rows)]),headers={'Content-Type':'application/octet-stream','X-CSRF-Token':c.csrf});return json.loads(c.opener.open(request).read())['definitions'][0]
        try:
            wait(ready,'independent sequence server');password=secrets.token_urlsafe(18);c.call('/api/auth/setup',dict(username='sequenceqa',name='序列测试',password=password));c.csrf=c.call('/api/auth/login',dict(username='sequenceqa',password=password))['csrf'];c.call('/api/control',dict(action='start'))
            check(not upload([['CALL','启动判读',j['Code'],'','','.2'],['','后续发送','K001','','','.2']],'execute')['errors'],'CALL to judgment is valid before later execution steps')
            check(any('不能' in v for v in upload([['CALL','禁止反向',e['Code']]],'judge')['errors']),'judgment cannot start an active sending sequence')
            check(any('最后' in v for v in upload([['CALL','同模式',et['Code']],['','后续','K001']],'execute')['errors']),'same-mode CALL retains its terminal jump constraint')
            start(e);wait(lambda:cfg()['runtimes']['judge']['running'],'CALL launches judgment');es,js=state(e),state(j)
            check(es['runtime']['running'] and js['runtime']['running'] and es['runtime']['runId']!=js['runtime']['runId'],'execution and launched judgment run at the same time with separate run IDs')
            check(es['runtime']['t0EstablishedAt']!=js['runtime']['t0EstablishedAt'] and js['runtime']['t0Elapsed']<es['runtime']['t0Elapsed'],'CALL target establishes its own T0 and starts a fresh clock')
            notice=es['call'];check(notice['sourceRunId']==es['runtime']['runId'] and notice['targetRunId']==js['runtime']['runId'] and notice['targetId']==j['Id'],'launch notification links both independent runs for the in-page judgment tab')
            payload,origin=receiver.recvfrom(65535);check(payload==b'\xaa\xbb\x01' and origin[1]==source,'execution sends its subsequent command through the real loopback UDP route')
            wait(lambda:any(r['status']=='等待参数' and r['input']['Condition'].endswith('==5') for r in state(e)['rows']),'execution waits for its own parameter')
            check(start(e,409)['error'].find('该模式')>=0,'a second run in the same mode is rejected without disturbing the other mode')
            stop(j);check(state(e)['runtime']['running'] and not state(j)['runtime']['running'],'stopping judgment leaves execution running')
            start(j);check(state(e)['runtime']['running'] and state(j)['runtime']['running'],'judgment can start manually while execution is running')
            old=state(e)['runtime'];c.call('/api/sequence/control',dict(action='pause',runId=old['runId'],controlRevision=old['controlRevision']));beforeE=state(e)['runtime'];beforeJ=state(j)['runtime'];time.sleep(.2)
            check(abs(state(e)['runtime']['elapsed']-beforeE['elapsed'])<.02 and state(j)['runtime']['elapsed']>beforeJ['elapsed']+.12,'pausing execution freezes only its own clock and leaves judgment timing active')
            data(99);wait(lambda:not state(j)['runtime']['running'],'judgment finishes while execution paused');check(state(e)['runtime']['paused'],'judgment receives fresh telemetry and completes while execution stays paused')
            paused=state(e)['runtime'];c.call('/api/sequence/control',dict(action='resume',runId=paused['runId'],controlRevision=paused['controlRevision']));data(5);wait(lambda:not state(e)['runtime']['running'],'execution resumes independently')
            check(state(e)['runtime']['abnormal']==0 and state(j)['runtime']['abnormal']==0,'both independent runs finish with separate successful results')
            start(j);original=state(j)['runtime']['runId'];start(e);wait(lambda:not state(e)['runtime']['running'],'busy judgment rejects CALL')
            check(state(j)['runtime']['running'] and state(j)['runtime']['runId']==original and state(e)['runtime']['abnormal']==1,'busy judgment is never replaced by CALL; launch failure is recorded only in execution')
            stop(j);start(j);start(ej);wait(lambda:state(et)['runtime']['paused'],'same-mode execution jump')
            check(state(j)['runtime']['running'] and state(et)['runtime']['id']==et['Id'],'same-mode terminal jump starts a fresh execution run without altering judgment')
            stop(et);stop(j)
            start(jt);start(et);wait(lambda:not state(jt)['runtime']['running'],'judge times out independently')
            check(state(et)['runtime']['paused'] and state(jt)['runtime']['abnormal']==2,'judgment timeout continues its own later steps while execution remains paused')
            stop(et);start(ec);wait(lambda:cfg()['runtimes']['judge']['running'],'command-watching judgment starts');receiver.recvfrom(65535);wait(lambda:not state(ec)['runtime']['running'] and not state(jc)['runtime']['running'],'captured command reaches independent judgment')
            check(state(jc)['runtime']['abnormal']==0 and any(r['status']=='通过' for r in state(jc)['rows']),'independent judgment observes the actual command sent by execution')
            start(ep);wait(lambda:cfg()['runtimes']['judge']['running'],'CALL inside parallel block');data(5);wait(lambda:not state(ep)['runtime']['running'],'parallel block completes');check(state(j)['runtime']['running'],'CALL also launches judgment from a parallel block and leaves it running after execution completes')
            c.call('/api/control',dict(action='stop'));check(not cfg()['runtimes']['execute']['running'] and not cfg()['runtimes']['judge']['running'],'stopping shared telemetry reception stops both dependent runtimes')
            for mode in ['execute','judge']:
                hist=c.call('/api/sequence/history?mode='+mode)['rows'];runs=c.call('/api/sequence/runs?mode='+mode)['rows'];rows=list(csv.reader(io.StringIO(c.opener.open(c.base+'/api/sequence/export.csv?mode='+mode).read().decode('utf-8-sig'))))
                check(hist and runs and all(v['Mode']==mode for v in hist+runs) and all(v[0]==('序列执行' if mode=='execute' else '序列判读') for v in rows[1:]),mode+' history, run search and CSV export are scoped to their own feature')
            events=[json.loads(line) for line in (root/'data/sequence-history.jsonl').read_text(encoding='utf-8-sig').splitlines() if line]
            starts={v['RunId'] for v in events if v['Event']=='开始'};ends=[v['RunId'] for v in events if v['Event']=='结束'];check(starts==set(ends) and len(ends)==len(set(ends)),'every independent run has exactly one persisted start and finish archive')
            (ROOT/'test-output/independent-sequences/integration-report.json').write_text(json.dumps(dict(checks=len(checks),details=checks),ensure_ascii=True,indent=2),encoding='utf-8')
            print('All',len(checks),'independent sequence checks passed.')
        finally:
            sender.close();receiver.close();proc.terminate();proc.wait(timeout=8)

if __name__=='__main__':main()
