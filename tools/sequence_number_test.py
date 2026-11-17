"""Sequence codes migrate safely, allocate independently and survive replacement/deletion/restart."""
import sys,json,tempfile,shutil,subprocess,secrets,urllib.request,urllib.parse
from pathlib import Path
from integration_test import ROOT,Client,free_port,wait,seed_send_monitor
from sequence_test import xlsx

def main():
    exe=Path(sys.argv[1]).resolve();checks=[]
    def check(ok,label):assert ok,label;checks.append(label);print('PASS',label,flush=True)
    with tempfile.TemporaryDirectory(dir=ROOT/'test-output',prefix='sequence-codes-') as scratch:
        root=Path(scratch);shutil.copytree(ROOT/'config',root/'config');shutil.copytree(ROOT/'web',root/'web');seed_send_monitor(root)
        for name in ['decoder.json','continuous-rules.json','tc-bindings.json','observed-commands.json','sequences.json']:(root/'config'/name).unlink(missing_ok=True)
        path=root/'config/sequences.json';legacy=[dict(Id=str(i)*32,Name='旧序列'+str(i),Mode=mode,Rows=[dict(Row=2,Keyword='TEXT',Description='保留说明')]) for i,mode in [(1,'judge'),(2,'execute'),(3,'judge')]]
        path.write_text(json.dumps(dict(SchemaVersion=1,Revision='old',DefaultTimeoutSeconds=30,Sequences=legacy)),encoding='utf-8');port=free_port();c=Client(port);password=secrets.token_urlsafe(18);process=None
        def config():return c.call('/api/sequence/config')
        def start():
            nonlocal process,c
            process=subprocess.Popen([str(exe),'--local','--root',str(root),'--web-port',str(port)],stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL,creationflags=subprocess.CREATE_NO_WINDOW);c=Client(port)
            def ready():
                try:c.call('/api/auth/me');return True
                except OSError:return False
            wait(ready,'numbered sequence server')
        def preview(name,mode='judge',rows=None):
            data=xlsx([('任务',rows or [['TEXT','说明','','','','','']])]);query=urllib.parse.urlencode(dict(filename=name+'.xlsx',mode=mode,timeout='30',sourceIP=''))
            return json.load(c.opener.open(urllib.request.Request(c.base+'/api/sequence/preview?'+query,data=data,headers={'Content-Type':'application/octet-stream','X-CSRF-Token':c.csrf})))
        def install(name,mode='judge',replace=False):
            p=preview(name,mode);c.call('/api/sequence/apply',dict(token=p['token'],ids=[p['definitions'][0]['definition']['Id']],mode='replace' if replace else 'add'));return next(d for d in config()['definitions'] if d['Name']==name+' / 任务')
        def edit(d,mode=None,delete=False):
            c.call('/api/sequence/config',dict(id=d['Id'],mode=mode or d['Mode'],sourceIP='',defaultTimeout='30',revision=config()['revision'],delete='true' if delete else 'false'))
        try:
            start();c.call('/api/auth/setup',dict(username='numberqa',name='QA',password=password));c.csrf=c.call('/api/auth/login',dict(username='numberqa',password=password))['csrf']
            check([d['Code'] for d in config()['definitions']]==['XLP0001','XLZ0001','XLP0002'],'legacy judge and execute sequences receive independent codes in existing order')
            check([d['Id'] for d in config()['definitions']]==[d['Id'] for d in legacy] and c.call('/api/sequence/definition?id='+legacy[0]['Id'])['definition']['Rows'][0]['Description']=='保留说明','migration preserves stable IDs, original names and rows')
            before=json.loads(path.read_text(encoding='utf-8-sig'));preview('只读');preview('只读');after=json.loads(path.read_text(encoding='utf-8-sig'));check(before==after,'repeated previews consume no codes or persist drafts')
            a=install('新增判读');b=install('新增执行','execute');check(a['Code']=='XLP0003' and b['Code']=='XLZ0002','saving allocates independently from each category counter')
            edit(a);check(next(d for d in config()['definitions'] if d['Id']==a['Id'])['Code']==a['Code'],'saving unchanged settings retains the code')
            edit(a,'execute');a=next(d for d in config()['definitions'] if d['Id']==a['Id']);check(a['Code']=='XLZ0003','changing mode allocates the correct new prefix without changing stable ID')
            edit(a,delete=True);d=install('删除后新增','execute');check(d['Code']=='XLZ0004','deleting a sequence never reuses its allocated code')
            replacement=install('删除后新增','execute',True);check(replacement['Code']==d['Code'] and replacement['Id']!=d['Id'],'same-name same-mode replacement retains its sequence code')
            e=install('替换后新增');check(e['Code']=='XLP0004','replace-all keeps independent high-water counters')
            saved=path.read_bytes();process.terminate();process.wait(timeout=5);start();c.csrf=c.call('/api/auth/login',dict(username='numberqa',password=password))['csrf'];check(path.read_bytes()==saved and next(d for d in config()['definitions'] if d['Id']==e['Id'])['Code']=='XLP0004','codes and counters survive restart without rewriting a current configuration')
            f=install('重启后新增');check(f['Code']=='XLP0005','allocation resumes after the prior maximum following restart')
        finally:
            if process and process.poll() is None:process.terminate();process.wait(timeout=5)
    print('All',len(checks),'sequence numbering checks passed. No network commands sent.')
if __name__=='__main__':main()
