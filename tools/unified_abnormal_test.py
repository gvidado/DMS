"""Unified persistent incidents, legacy compatibility and mandatory reason releases."""
import csv, io, json, secrets, shutil, subprocess, sys, tempfile
from pathlib import Path
from urllib.parse import urlencode
from integration_test import ROOT, Client, free_port, wait, seed_send_monitor


def seed(root):
    (root/'data').mkdir(exist_ok=True)
    judgment=[dict(Kind='abnormal',Id=1,Time='2026-11-01 10:00:00.000',Code='A4',Name='参数,"甲"\n保留',ValueText='-6',EngineeringValue='-3',Result='Error',Script='VAL=this.value*2;\nRANGE=[0 1];',SourceIP='127.0.0.1',Packet='测试SPP',Mode='实时UDP'),
              dict(Kind='abnormal',Id=2,Time='2026-11-01 10:00:01.000',Code='A5',Name='参数乙',ValueText='2',EngineeringValue='2',Result='执行异常',Detail='除数为零',Script='VAL=1/0;',SourceIP='127.0.0.1'),
              dict(Kind='abnormal',Id=3,Time='2026-11-01 10:00:02.000',Code='A6',Name='已解除',ValueText='5',EngineeringValue='5',Result='Error',Script='RET=Error;'),
              dict(Kind='abnormal',Id=4,Time='2026-11-01 10:00:03.000',Code='A7',Name='告警参数',ValueText='8',EngineeringValue='8',Result='Unusual',Script='RET=Unusual;',SourceIP='127.0.0.1'),
              dict(Kind='release',Id=3,Time='2026-11-01 11:00:00.000',Reason='原解除原因',Username='old',Actor='原操作人')]
    alarms=[]
    def alarm(category,message,**extra):
        alarms.append(dict(Kind='alarm',Id=len(alarms)+1,Time='2026-11-01 10:00:00.100',Level=extra.pop('Level','error'),Category=category,Message=message,Source='127.0.0.1',Apid=-1,Sequence=-1,**extra))
    alarm('常态化判读','重要 / TM.A4 / 参数,"甲"\n保留：Error，工程值=-3')
    alarm('判读规则异常','重要 / A5：除数为零')
    alarm('SPP异常','校验错误',Packet='测试SPP')
    alarm('序列执行异常','序列甲 / 第3行：超时',ContextCode='第3行',ContextName='序列甲',ContextValue='{"TM.A4.value":-3}',ContextRule='TM.A4.value>0',RunId='run-execute')
    alarm('序列判读异常','序列乙 / 第4行：HOLD不满足',ContextCode='第4行',ContextName='序列乙',ContextValue='{}',ContextRule='INENUM(TM.A1.value)',RunId='run-judge')
    alarm('常态化判读','旧版孤立告警仍保留')
    alarm('SPP异常','无匹配的SPP包标识',Packet='未识别')
    for i in range(60):alarm('重复帧' if i%3==0 else 'SPP超时',f'分页测试{i}',Packet=f'SPP{i}',Level='warn' if i%3==0 else 'error')
    original=list(alarms)
    alarms.append(dict(Kind='ack-one',Id=3,Time='2026-11-01 12:00:00.000',Actor='旧确认人',Username='old'))
    def write(path,rows):path.write_text(''.join(json.dumps(r,ensure_ascii=False)+'\n' for r in rows),encoding='utf-8')
    write(root/'data/judgment-abnormal-history.jsonl',judgment)
    write(root/'data/alarm-history.jsonl',alarms)
    return len(original)-3+4  # two matched summaries and one non-alarm are omitted


def main():
    executable=Path(sys.argv[1]).resolve();checks=[]
    def check(ok,label):assert ok,label;checks.append(label);print('PASS',label,flush=True)
    with tempfile.TemporaryDirectory(prefix='unified-incidents-',dir=ROOT/'test-output') as temporary:
        root=Path(temporary);shutil.copytree(ROOT/'config',root/'config');shutil.copytree(ROOT/'web',root/'web');seed_send_monitor(root)
        total=seed(root);port=free_port();password=secrets.token_urlsafe(18)
        def start():return subprocess.Popen([str(executable),'--local','--root',str(root),'--web-port',str(port)],stdout=subprocess.DEVNULL,stderr=subprocess.STDOUT)
        process=start();c=Client(port)
        def ready():
            try:c.call('/api/auth/me');return True
            except OSError:return False
        def page(**query):return c.call('/api/abnormal-records?'+urlencode(query))
        def release(key,reason,expected=200,csrf=True):return c.call('/api/abnormal-records/release',dict(key=key,reason=reason),expected=expected,csrf=csrf)
        try:
            wait(ready,'unified history server');c.call('/api/abnormal-records',expected=401);c.call('/api/abnormal-records/export.csv',expected=401)
            c.call('/api/auth/setup',dict(username='unifiedqa',name='统一记录测试',password=password));c.csrf=c.call('/api/auth/login',dict(username='unifiedqa',password=password))['csrf']
            paths=[root/'data/alarm-history.jsonl',root/'data/judgment-abnormal-history.jsonl'];before=[p.read_bytes() for p in paths]
            result=page(pageSize=100);rows={r['key']:r for r in result['records']}
            check(result['total']==total and result['pending']==total-1 and result['released']==1,'unifies all incidents and omits matched continuous alarm summaries only')
            check('alarm:1' not in rows and 'alarm:2' not in rows and 'alarm:7' not in rows and rows['judge:1']['value']=='-6','matches original engineering value rather than transformed VAL and excludes unknown SPP information')
            check(rows['alarm:3']['legacyConfirmed'] and not rows['alarm:3']['released'] and rows['alarm:3']['legacyConfirmedBy']=='旧确认人','old confirmation is preserved but requires a release reason')
            check(rows['alarm:4']['type']=='sequence-execute' and rows['alarm:4']['rule']=='TM.A4.value>0' and rows['alarm:5']['type']=='sequence-judge' and rows['alarm:5']['runId']=='run-judge','sequence row, rule and run context are retained')
            check(rows['judge:3']['reason']=='原解除原因' and rows['judge:3']['released'],'previous reasoned releases retain status and reason')
            check(page(type='continuous')['matching']==5 and page(type='sequence-execute')['matching']==1 and page(type='sequence-judge')['matching']==1 and page(search='序列甲')['matching']==1,'category and keyword filters include legacy standalone incidents')
            warnings=page(level='warn',pageSize=100);errors=page(level='error',pageSize=100)
            check(warnings['matching']==21 and errors['matching']==total-21 and all(r['level']=='warn' for r in warnings['records']) and all(r['level']=='error' for r in errors['records']),'warning/error filter covers all history and keeps Unusual separate from faults')
            check(page(level='warn',type='continuous')['matching']==1 and page(level='warn',type='continuous')['records'][0]['key']=='judge:4' and page(level='error',status='released')['matching']==1 and page(level='warn',type='sequence-execute')['matching']==0,'severity combines with source type and release status including an empty combination')
            warning_first=page(level='warn',pageSize=7);warning_second=page(level='warn',pageSize=7,page=2,judgmentThrough=warning_first['judgmentThrough'],alarmThrough=warning_first['alarmThrough'])
            check(warning_first['pages']==3 and len(warning_second['records'])==7 and not ({r['key'] for r in warning_first['records']}&{r['key'] for r in warning_second['records']}) and all(r['level']=='warn' for r in warning_second['records']),'severity filtering happens before pagination with stable source cursors')
            first=page(pageSize=10);second=page(page=2,pageSize=10,judgmentThrough=first['judgmentThrough'],alarmThrough=first['alarmThrough'])
            check(len(first['records'])==10 and len(second['records'])==10 and not ({r['key'] for r in first['records']}&{r['key'] for r in second['records']}),'mixed-source pagination has distinct stable page anchors')
            check(page(knownRevision=result['revision'])['unchanged'],'unchanged poll returns a compact response')
            for query in ['page=0','pageSize=101','type=bad','status=bad','alarmThrough=-1','level=bad','level=bad&knownRevision='+result['revision']]:c.call('/api/abnormal-records?'+query,expected=400)
            for reason in ['', ' \n ', 'x'*2001]:release('alarm:3',reason,400)
            release('alarm:3','不允许',403,False)
            for key in ['alarm:03','judge:0','bad:1','alarm:999','alarm:1']:release(key,'无效',400)
            check([p.read_bytes() for p in paths]==before,'reads and rejected operations preserve original histories byte-for-byte')
            reason='已核实,"人工解除"\n多行原因'
            for key in ['alarm:3','alarm:4','alarm:5','judge:1','judge:2']:release(key,reason)
            release('alarm:3','覆盖',409);release('judge:1','覆盖',409)
            updated=page(pageSize=100);current={r['key']:r for r in updated['records']}
            check(updated['released']==6 and all(current[key]['reason']==reason and current[key]['releasedBy']=='统一记录测试' and current[key]['releasedAt'] for key in ['alarm:3','alarm:4','alarm:5','judge:1','judge:2']),'every category releases individually with mandatory reason, actor and timestamp')
            check(not page(knownRevision=result['revision'])['unchanged'] and page(status='released')['matching']==6,'release invalidates revision and updates status filters')
            general=c.call('/api/alarms/list?pageSize=100');check(all(r['confirmed'] for r in general['records'] if r['id'] in (1,2,3,4,5)),'releasing incidents also confirms their original general alarm summaries')
            with c.opener.open(c.base+'/api/abnormal-records/export.csv') as response:raw=response.read()
            exported=list(csv.DictReader(io.StringIO(raw.decode('utf-8-sig'),newline='')))
            check(raw.startswith(b'\xef\xbb\xbf') and len(exported)==total and len(exported[0])==23 and list(exported[0])[:6]==['参数代号','参数名称','异常值','是否解除','解除原因','判读规则'],'unified CSV exports all categories in 23 columns preserving the first six fields')
            row=next(r for r in exported if r['记录号']=='judge:1');check(row['异常值']=='-6' and row['解除原因']==reason and row['参数名称']=='参数,"甲"\n保留' and '\n' in row['判读规则'],'CSV preserves negative numbers, multiline scripts, Chinese names and reasons')
            for invalid in ['', ' \n ', 'x'*2001]:
                c.call('/api/abnormal-records/release-all',dict(reason=invalid),expected=400)
            c.call('/api/abnormal-records/release-all',dict(reason='未授权'),expected=403,csrf=False)
            old_releases={r['key']:r['reason'] for r in page(pageSize=100)['records'] if r['released']}
            old_pending=page()['pending'];common='已统一核实，批量解除所有类型和分页'
            if '--browser' in sys.argv:
                node_root=Path(sys.executable).parent.parent/'node'
                browser=subprocess.run([str(node_root/'bin/node.exe'),str(ROOT/'tools/unified_abnormal_browser.mjs'),str(node_root)],
                    input=json.dumps(dict(base=c.base,password=password,pending=old_pending,reason=common,output=str(executable.parent))),
                    capture_output=True,text=True,encoding='utf-8',timeout=100,creationflags=subprocess.CREATE_NO_WINDOW)
                print(browser.stdout,flush=True);assert browser.returncode==0,browser.stderr
                bulk=json.loads((executable.parent/'batch-release-browser-report.json').read_text(encoding='utf-8'))['release']
            else:bulk=c.call('/api/abnormal-records/release-all',dict(reason=common))
            all_rows=page(pageSize=100)
            check(bulk['ok'] and bulk['released']==old_pending and bulk['remaining']==0 and all_rows['pending']==0 and all_rows['total']==total,'one operation releases pending continuous, SPP, sequence and system records across every page')
            check(all(r['released'] and r['reason']==old_releases.get(r['key'],common) and r['releasedAt'] and r['releasedBy'] for r in all_rows['records']),'batch releases preserve prior reasons and store the common reason, actor and time on every newly released record')
            empty=c.call('/api/abnormal-records/release-all',dict(reason='不覆盖原原因'))
            check(empty['released']==0 and all(r['reason']==old_releases.get(r['key'],common) for r in page(pageSize=100)['records']),'repeated release-all is idempotent and never overwrites existing release reasons')
            stable={r['key']:r for r in page(pageSize=100)['records']};process.terminate();process.wait(timeout=6)
            sidecar=root/'data/abnormal-releases.jsonl';sidecar.write_bytes(sidecar.read_bytes()+b'{"Key":"alar')
            process=start();c=Client(port);wait(ready,'restart unified history server');c.csrf=c.call('/api/auth/login',dict(username='unifiedqa',password=password))['csrf']
            restarted={r['key']:r for r in page(pageSize=100)['records']}
            check(all(restarted[key]['reason']==row['reason'] and restarted[key]['released']==row['released'] for key,row in stable.items()) and list((root/'data').glob('abnormal-releases.jsonl.recovered-*.bak')),'release reasons survive restart and incomplete-tail recovery creates a backup')
            check(c.call('/api/state')['mode']=='stopped','server restart does not start listening or send commands')
            process.terminate();process.wait(timeout=6);corrupt=sidecar.read_bytes()+b'{"Key":null,"Reason":"x"}\n';sidecar.write_bytes(corrupt)
            process=start();c=Client(port);wait(ready,'corrupt sidecar startup');c.csrf=c.call('/api/auth/login',dict(username='unifiedqa',password=password))['csrf']
            check(page()['storageError'] and sidecar.read_bytes()==corrupt,'complete invalid release history is preserved and surfaced instead of silently reset')
            release('alarm:9','不能丢失历史',409);check(sidecar.read_bytes()==corrupt,'storage failure blocks release without overwriting corrupted history')
        finally:
            if process.poll() is None:process.terminate();process.wait(timeout=6)
    print(f'All {len(checks)} unified incident checks passed.')


if __name__=='__main__':main()
