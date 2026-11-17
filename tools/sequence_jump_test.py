"""CALL is a terminal jump, with separate runs, reset clock/T0 and real loopback sends."""
import sys,json,tempfile,shutil,subprocess,secrets,urllib.request,urllib.parse,socket,time
from pathlib import Path
from integration_test import ROOT,Client,free_port,wait,seed_send_monitor
from sequence_test import xlsx

def main():
    exe=Path(sys.argv[1]).resolve();checks=[]
    def check(ok,label):assert ok,label;checks.append(label);print('PASS',label,flush=True)
    with tempfile.TemporaryDirectory(dir=ROOT/'test-output',prefix='sequence-jumps-') as scratch:
        root=Path(scratch);shutil.copytree(ROOT/'config',root/'config');shutil.copytree(ROOT/'web',root/'web');seed_send_monitor(root)
        for name in ['decoder.json','continuous-rules.json','tc-bindings.json','observed-commands.json','sequences.json']:(root/'config'/name).unlink(missing_ok=True)
        def save(name,value):(root/'config'/name).write_text(json.dumps(value),encoding='utf-8')
        settings=json.loads((root/'config/server.json').read_text(encoding='utf-8-sig'));settings.update(BindAddress='127.0.0.1',UdpPort=free_port(socket.SOCK_DGRAM),SourceIPs=['127.0.0.1'],MappingConfirmed=True,MulticastGroup='',MulticastInterface='',DataTimeoutMs=60000);save('server.json',settings)
        save('observed-commands.json',dict(SchemaVersion=1,SourceIP='127.0.0.1',IdentityRule='udp-payload',NextNumber=3,Commands=[dict(Number=i,Name='指令'+str(i),DestinationIP='127.0.0.2',PayloadHex=f'AA BB {i:02X}',Length=3) for i in [1,2]]))
        save('tc-bindings.json',dict(SchemaVersion=1,Revision='test',Bindings=[dict(Code='K00'+str(i),Number=i) for i in [1,2]]))
        udp=socket.socket(socket.AF_INET,socket.SOCK_DGRAM);udp.bind(('127.0.0.2',0));udp.settimeout(4)
        save('command-send.json',dict(SchemaVersion=1,SourceIP='127.0.0.1',Routes=[dict(DestinationIP='127.0.0.2',SourcePort=free_port(socket.SOCK_DGRAM),DestinationPort=udp.getsockname()[1])]))
        port=free_port();c=Client(port);password=secrets.token_urlsafe(16)
        process=subprocess.Popen([str(exe),'--local','--root',str(root),'--web-port',str(port)],stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL,creationflags=subprocess.CREATE_NO_WINDOW)
        def ready():
            try:c.call('/api/auth/me');return True
            except OSError:return False
        def config():return c.call('/api/sequence/config')
        def preview(name,rows,mode='judge',sheets=None):
            query=urllib.parse.urlencode(dict(filename=name+'.xlsx',mode=mode,timeout='3',sourceIP='127.0.0.1'))
            return json.load(c.opener.open(urllib.request.Request(c.base+'/api/sequence/preview?'+query,data=xlsx(sheets or [('任务',rows)]),headers={'Content-Type':'application/octet-stream','X-CSRF-Token':c.csrf})))
        def install(name,rows,mode='judge'):
            p=preview(name,rows,mode);assert not p['definitions'][0]['errors'],p['definitions'][0]['errors'];c.call('/api/sequence/apply',dict(token=p['token'],ids=[p['definitions'][0]['definition']['Id']],mode='add'));return next(d for d in config()['definitions'] if d['Id']==p['definitions'][0]['definition']['Id'])
        def start(d):return c.call('/api/sequence/control',dict(action='start',id=d['Id'],runName='自定义'+d['Name'],requestId=secrets.token_hex(16),revision=config()['revision']))
        def row(key='',desc='',cmd='',condition='',mark='',expected='',timeout=''):return [key,desc,cmd,condition,mark,expected,timeout]
        try:
            wait(ready,'CALL server');c.call('/api/auth/setup',dict(username='jumpqa',name='QA',password=password));c.csrf=c.call('/api/auth/login',dict(username='jumpqa',password=password))['csrf']
            target=install('判读目标',[row('TEXT','目标完成')]);source=install('判读来源',[row('TEXT','来源'),row('CALL','跳转',target['Code'])])
            for rows in [[row('CALL','缺失','XLP9999')],[row('CALL','不是末尾',target['Code']),row('','后续','K001')],[row('PAR'),row('CALL','并行',target['Code']),row('CHANGE')]]:
                check(bool(preview('错误',rows)['definitions'][0]['errors']),'missing, nonterminal or parallel CALL is rejected with preview errors')
            exec_target=install('执行目标',[row('PLUSE','跳转后暂停'),row('','新T0','K001','','T0','0'),row('','新T0调度','K002','','','T0:0.5')],'execute')
            check(any('不能' in e for e in preview('跨模式',[row('CALL','跨模式',exec_target['Code'])])['definitions'][0]['errors']),'judgment sequences reject jumps to execution sequences')
            unbased=preview('没有新T0',[row('','不能继承','K001','','','T0:1')],'execute');check(bool(unbased['definitions'][0]['errors']),'T0 references need a marker in the target sequence itself')
            c.call('/api/control',dict(action='start'));start(source)
            wait(lambda:not config()['runtime']['running'],'passive chain finished');runtime=config()['runtime'];history=c.call('/api/sequence/history')['rows'];begins=[e for e in history if e['Event']=='开始'];ends=[e for e in history if e['Event']=='结束']
            check(runtime['id']==target['Id'] and len(begins)==2 and len({e['RunId'] for e in begins})==2 and len(ends)==2,'CALL ends the source and starts a separate target run without returning')
            check(any('CALL转至'+target['Code'] in e['Reason'] for e in ends),'source completion history records the jump destination')
            active=install('执行来源',[row('','原T0','K001','','T0','0'),row('CALL','转移',exec_target['Code'],'','','T0:1')],'execute')
            started=start(active);first=udp.recvfrom(100)[0];assert first==bytes.fromhex('AA BB 01')
            wait(lambda:config()['runtimes']['execute']['id']==exec_target['Id'] and config()['runtimes']['execute']['paused'],'target pause')
            state=config()['runtimes']['execute'];check(state['runId']!=started['runId'] and state['t0'] is None and state['elapsed']<.15,'target starts its own run with elapsed near zero and no inherited source T0')
            time.sleep(.2);check(abs(config()['runtimes']['execute']['elapsed']-state['elapsed'])<.03,'pause freezes the target clock')
            c.call('/api/sequence/control',dict(action='resume',runId=state['runId'],controlRevision=str(state['controlRevision'])))
            assert udp.recvfrom(100)[0]==bytes.fromhex('AA BB 01');assert udp.recvfrom(100)[0]==bytes.fromhex('AA BB 02')
            wait(lambda:not config()['runtimes']['execute']['running'],'execute target finished');history=c.call('/api/sequence/history')['rows'];target_rows=[e for e in history if e['Code']==exec_target['Code'] and e['Event']=='步骤结果'];last=next(e for e in target_rows if e['Input']['Command']=='K002');marked=next(e for e in target_rows if e['Input']['Mark']=='T0')
            check(abs(last['Planned']-marked['Elapsed']-.5)<.02 and last['Actual']>=last['Planned'],'target T0:n uses its own marker completion and scheduling')
            check(len([e for e in history if e['Event']=='指令发送'])==3,'CALL itself sends no UDP and never repeats source commands')
            c.call('/api/control',dict(action='stop'))
            pair=preview('循环测试',[],sheets=[('A',[row('TEXT')]),('B',[row('TEXT')])]);c.call('/api/sequence/apply',dict(token=pair['token'],ids=[d['definition']['Id'] for d in pair['definitions']],mode='add'))
            saved=config()['definitions'];a=next(d for d in saved if d['Name']=='循环测试 / A');b=next(d for d in saved if d['Name']=='循环测试 / B')
            cycle=preview('循环测试',[],sheets=[('A',[row('CALL','',b['Code'])]),('B',[row('CALL','',a['Code'])])]);response=c.call('/api/sequence/apply',dict(token=cycle['token'],ids=[d['definition']['Id'] for d in cycle['definitions']],mode='replace'),expected=400)
            check('循环' in response['error'] and config()['definitions']==saved,'candidate replacement detects cycles and preserves the original configuration atomically')
            check(not preview('执行启动判读',[row('CALL','启动判读',target['Code'])],'execute')['definitions'][0]['errors'],'execution CALL accepts independent judgment targets')
        finally:process.terminate();process.wait(timeout=5);udp.close()
    print('All',len(checks),'CALL jump checks passed. UDP used loopback only.')
if __name__=='__main__':main()
