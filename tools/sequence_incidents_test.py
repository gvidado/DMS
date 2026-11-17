"""Sequence incident filtering and shared reasoned confirmation in isolated storage."""
import csv,io,json,secrets,shutil,subprocess,sys,tempfile
from pathlib import Path
from urllib.parse import urlencode
from integration_test import ROOT,Client,free_port,wait

def seed(root):
    (root/'data').mkdir(exist_ok=True);events=[];keys=[]
    for index,count in [(1,55),(2,2),(3,1)]:
        run=chr(96+index)*32;mode='execute' if index==3 else 'judge';name='同名序列' if index<3 else '执行序列'
        def event(kind,number=0):
            e=dict(Event=kind,RunId=run,Mode=mode,Code='XLZ0001' if index==3 else 'XLP0001',Sequence=name,Sheet='测试',Time=f'2026-11-06 10:0{index}:{number:02}.000',Status='异常' if kind in ['异常','步骤结果'] else '运行中' if kind=='开始' else '完成，存在异常',Row=3 if number else 0,Reason='HOLD持续判据不满足' if number else '',Values='{"TME.A1.value":-1}')
            if number:e['Input']=dict(Row=3,Keyword='HOLD',Condition='TME.A1.value>0',Description='检查参数')
            events.append(e);return e
        event('开始')
        for number in range(1,count+1):event('异常',number);keys.append('sequence:'+str(len(events)));event('步骤结果',number)
        event('结束',59)
    path=root/'data/sequence-history.jsonl';path.write_text(''.join(json.dumps(e,ensure_ascii=False)+'\n' for e in events),encoding='utf-8')
    incidents=[e for e in events if e['Event']=='异常'];alarms=[]
    for i,e in enumerate([incidents[0],incidents[-1]]):
        alarms.append(dict(Kind='alarm',Id=i+1,Level='error',Category='序列执行异常' if e['Mode']=='execute' else '序列判读异常',Time=e['Time'].replace('.000','.100'),Message=e['Sequence']+' / 第3行：'+e['Reason'],Source='127.0.0.1',Apid=-1,Sequence=-1,ContextCode='第3行',ContextName=e['Sequence'],ContextRule=e['Input']['Condition'],RunId=e['RunId']))
    alarms.append(dict(Kind='alarm',Id=3,Level='error',Category='序列判读异常',Time='2026-10-01 00:00:00.000',Message='旧孤立告警',Source='',Apid=-1,Sequence=-1,RunId='old'))
    (root/'data/alarm-history.jsonl').write_text(''.join(json.dumps(e,ensure_ascii=False)+'\n' for e in alarms),encoding='utf-8')
    (root/'data/abnormal-releases.jsonl').write_text(json.dumps(dict(Key='alarm:1',Reason='原有确认原因',Time='2026-11-06 11:00:00.000',Username='old',Actor='旧操作人'),ensure_ascii=False)+'\n',encoding='utf-8')
    return keys

def main():
    exe=Path(sys.argv[1]).resolve();checks=[]
    def check(ok,label):assert ok,label;checks.append(label);print('PASS',label,flush=True)
    with tempfile.TemporaryDirectory(dir=ROOT/'test-output',prefix='sequence-incidents-') as temp:
        root=Path(temp);shutil.copytree(ROOT/'config',root/'config');shutil.copytree(ROOT/'web',root/'web');keys=seed(root);port=free_port();password=secrets.token_urlsafe(20);process=None;c=None
        def start():
            nonlocal process,c
            process=subprocess.Popen([str(exe),'--local','--root',str(root),'--web-port',str(port)],stdout=subprocess.DEVNULL,stderr=subprocess.STDOUT,creationflags=subprocess.CREATE_NO_WINDOW);c=Client(port)
            def ready():
                try:c.call('/api/auth/me');return True
                except OSError:return False
            wait(ready,'sequence incident server')
        def history(**q):return c.call('/api/sequence/history?'+urlencode(q))
        def unified(**q):return c.call('/api/abnormal-records?'+urlencode(q))
        def release(key,reason,expected=200,csrf=True):return c.call('/api/abnormal-records/release',dict(key=key,reason=reason),expected=expected,csrf=csrf)
        def exported(**q):return list(csv.DictReader(io.StringIO(c.opener.open(c.base+'/api/sequence/export.csv?'+urlencode(q)).read().decode('utf-8-sig'))))
        try:
            start();c.call('/api/auth/setup',dict(username='seqincidents',name='序列异常测试',password=password));c.csrf=c.call('/api/auth/login',dict(username='seqincidents',password=password))['csrf'];path=root/'data/sequence-history.jsonl';before=path.read_bytes()
            first=history(result='abnormal');second=history(result='abnormal',page=2)
            check(first['total']==58 and first['pages']==2 and len(first['rows'])==50 and len(second['rows'])==8,'abnormal filter selects incident episodes before pagination, without duplicate step summaries')
            last=history(result='abnormal',page=999);check(last['page']==2 and len(last['rows'])==8,'confirmation or filter changes cannot leave the page past the last available page')
            records=first['rows']+second['rows'];check(len({r['Abnormal']['key'] for r in records})==58,'every episode has an independent stable incident identity')
            check(history(result='abnormal',mode='judge')['total']==57 and history(result='abnormal',mode='execute')['total']==1,'execution and judgment abnormal filters are independent')
            check(history(result='abnormal',runId='b'*32,search='XLP0001')['total']==2 and history(result='abnormal',runId='b'*32,search='missing')['total']==0,'run, sequence search and abnormal constraints combine')
            check(history(result='released')['total']==1 and history(result='pending')['total']==57,'existing reasoned confirmations migrate to the matching incident')
            u=unified(pageSize=100);check(u['total']==59 and len([r for r in u['records'] if r['key'].startswith('sequence:')])==58 and any(r['key']=='alarm:3' for r in u['records']),'unified view merges matching alarm summaries and keeps standalone legacy alarms')
            check(path.read_bytes()==before,'reading and legacy reconciliation do not rewrite the original sequence history')
            for q in ['result=bad','result=pending&page=0']:c.call('/api/sequence/history?'+q,expected=400)
            target=keys[-3]
            for reason in ['', '  \n ', 'x'*2001]:release(target,reason,400)
            release(target,'forbidden',403,False);release('sequence:1','not abnormal',400)
            reason='核实原因,"保留"\n多行说明';release(target,reason);release(target,'overwrite',409)
            check(history(result='released',runId='b'*32)['rows'][0]['Abnormal']['reason']==reason and history(result='pending',runId='b'*32)['total']==1,'individual confirmation requires a reason and preserves other pending incidents')
            release('alarm:2','通过旧入口确认');check(history(mode='execute',result='released')['total']==1,'legacy alarm confirmation routes to the shared canonical incident')
            csvrows=exported(result='released',mode='judge');check(len(csvrows)==2 and all(len(r)==11 and r['模式']=='序列判读' and r['事件']=='异常' and '确认原因' not in r for r in csvrows),'filtered CSV keeps only the requested eleven columns while confirmation metadata stays in the shared incident history')
            check(all(r['事件']=='异常' for r in exported(result='abnormal')) and len(exported(result='pending'))==55,'CSV uses the same abnormal/status filters as the page')
            check(path.read_bytes()==before,'confirmations append to the shared release journal without rewriting step history')
            if len(sys.argv)>3:
                options=root/'browser.json';options.write_text(json.dumps(dict(base=c.base,username='seqincidents',password=password,runId='b'*32)),encoding='utf-8')
                subprocess.run([sys.argv[2],str(ROOT/'tools/sequence_incidents_browser.mjs'),sys.argv[3],str(options)],check=True)
            stable={r['key']:r for r in unified(pageSize=100)['records']};process.terminate();process.wait(timeout=6);start();c.csrf=c.call('/api/auth/login',dict(username='seqincidents',password=password))['csrf'];restarted={r['key']:r for r in unified(pageSize=100)['records']}
            check(all(restarted[key]['released']==r['released'] and restarted[key]['reason']==r['reason'] for key,r in stable.items()),'confirmations and stable episode identities survive restart')
            prior=unified()['revision'];new=dict(Event='异常',RunId='d'*32,Mode='judge',Sequence='追加运行',Code='XLP0002',Time='2026-11-06 12:00:00.000',Status='超时',Row=2,Reason='条件超时')
            with path.open('a',encoding='utf-8') as f:f.write(json.dumps(new,ensure_ascii=False)+'\n')
            check(not unified(knownRevision=prior)['unchanged'] and history(result='abnormal')['total']==59,'new sequence-only incidents refresh unified polling without requiring a duplicate general alarm')
            print('All',len(checks),'sequence incident checks passed.',flush=True)
        finally:
            if process and process.poll() is None:process.terminate();process.wait(timeout=6)
if __name__=='__main__':main()
