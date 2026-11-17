"""Sparse per-record acknowledgements, legacy bulk history, pagination and CSV."""
import csv,io,json,random,secrets,shutil,subprocess,sys,tempfile
from pathlib import Path
from integration_test import ROOT,Client,free_port,wait,seed_send_monitor

def main():
    exe=Path(sys.argv[1]).resolve();checks=[]
    def check(ok,name):assert ok,name;checks.append(name);print('PASS',name,flush=True)
    with tempfile.TemporaryDirectory(prefix='alarm-single-',dir=ROOT/'test-output') as temporary:
        root=Path(temporary);shutil.copytree(ROOT/'config',root/'config');shutil.copytree(ROOT/'web',root/'web');seed_send_monitor(root);(root/'data').mkdir()
        journal=root/'data/alarm-history.jsonl';entries=[];visible=[]
        for n in range(1,138):
            visible_alarm=n%7!=0
            entries.append(dict(Kind='alarm',Id=n,Time='2026-11-02 10:00:00',Level='warn' if visible_alarm else 'info',Category='SPP异常' if visible_alarm else '运行信息',Message='异常'+str(n),Source='127.0.0.1',Packet='Sheet1',Apid=8))
            if visible_alarm:visible.append(n)
            if n==3:entries.append(dict(Kind='ack',ThroughId=3,Time='2026-11-02 10:01:00',Username='old',Actor='历史确认人'))
        journal.write_text(''.join(json.dumps(e,ensure_ascii=False)+'\n' for e in entries),encoding='utf-8');expected={n:'历史确认人' for n in visible if n<=3};port=free_port();password=secrets.token_urlsafe(18)
        def start():return subprocess.Popen([str(exe),'--local','--root',str(root),'--web-port',str(port)],stdout=subprocess.DEVNULL,stderr=subprocess.STDOUT)
        process=start();c=Client(port)
        def ready():
            try:c.call('/api/auth/me');return True
            except OSError:return False
        def ack(client,n,expected_status=200,csrf=True):return client.call('/api/alarms/ack',dict(id=n),expected=expected_status,csrf=csrf)
        def verify(client,anchor=0):
            for status in ['all','pending','confirmed']:
                ids=[n for n in visible if (not anchor or n<=anchor) and (status=='all' or (n in expected)==(status=='confirmed'))][::-1];actual=[]
                url='/api/alarms/list?pageSize=13&status='+status+'&throughId='+str(anchor);first=client.call(url)
                assert (first['total'],first['confirmed'],first['pending'],first['matching'])==(len(visible),len(expected),len(visible)-len(expected),len(ids))
                for page in range(1,first['pages']+1):actual+=client.call(url+'&page='+str(page))['records']
                assert [r['id'] for r in actual]==ids
                for row in actual:assert row['confirmed']==(row['id'] in expected) and row['confirmedBy']==expected.get(row['id'],'')
        try:
            wait(ready,'single alarm server');c.call('/api/auth/setup',dict(username='single_admin',name='单条确认人',password=password));c.csrf=c.call('/api/auth/login',dict(username='single_admin',password=password))['csrf'];verify(c)
            original=journal.read_bytes();Client(port).call('/api/alarms/ack',dict(id=137),expected=401);ack(c,137,403,False)
            for n in [0,-1,9999,7,'1.5']:ack(c,n,400)
            check(journal.read_bytes()==original,'unauthenticated, CSRF-less, absent, hidden or invalid IDs cannot change history')
            ack(c,113);expected[113]='单条确认人';verify(c);verify(c,70);check(True,'one acknowledgement changes only its ID, including anchored pages and sparse filters')
            first=next(r for r in c.call('/api/alarms/list?status=confirmed')['records'] if r['id']==113);before=journal.read_bytes();second_password=secrets.token_urlsafe(18);c.call('/api/users',dict(username='second_ack',name='第二确认人',password=second_password));second=Client(port);second.csrf=second.call('/api/auth/login',dict(username='second_ack',password=second_password))['csrf'];ack(second,113)
            check(journal.read_bytes()==before and next(r for r in c.call('/api/alarms/list?status=confirmed')['records'] if r['id']==113)==first,'repeated confirmation is idempotent and keeps the first actor/time')
            targets=random.Random(23).sample([n for n in visible if n not in expected],45)
            for i,n in enumerate(targets):client=c if i%2==0 else second;ack(client,n);expected[n]='单条确认人' if i%2==0 else '第二确认人'
            verify(c);verify(c,70);check(True,'out-of-order acknowledgements by multiple users maintain exact pagination and counters')
            raw=c.opener.open(c.base+'/api/alarms/export').read();rows=list(csv.DictReader(io.StringIO(raw.decode('utf-8-sig'))));check(len(rows)==len(visible) and all(r['确认状态']==('已确认' if int(r['记录号']) in expected else '未确认') and r['确认人']==expected.get(int(r['记录号']),'') for r in rows),'CSV contains every record and its individual confirmation actor/status')
            second.call('/api/control',dict(action='ack'));expected.update({n:expected.get(n,'第二确认人') for n in visible});verify(c);check(next(r for r in c.call('/api/alarms/list?pageSize=100')['records'] if r['id']==113)['confirmedAt']==first['confirmedAt'],'bulk confirmation fills pending gaps without replacing individual first confirmations')
            process.terminate();process.wait(timeout=6);process=start();c=Client(port);wait(ready,'single alarm restart');c.csrf=c.call('/api/auth/login',dict(username='single_admin',password=password))['csrf'];verify(c);check(True,'mixed legacy, single and bulk confirmation records survive restart without rewriting alarm IDs')
        finally:
            if process.poll() is None:process.terminate();process.wait(timeout=6)
    print('All',len(checks),'single alarm checks passed.')
if __name__=='__main__':main()
