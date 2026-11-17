"""V0.9.3 TEXT scheduling, latest-stream ship time and documentation migration."""
import json,secrets,shutil,socket,subprocess,sys,tempfile,time,urllib.request
from pathlib import Path
from integration_test import ROOT,Client,free_port,seed_send_monitor
from sequence_test import xlsx

def main():
    exe=Path(sys.argv[1]).resolve();checks=[]
    def check(ok,label):assert ok,label;checks.append(label);print('PASS',label,flush=True)
    def until(fn,label):
        end=time.monotonic()+8
        while time.monotonic()<end:
            value=fn()
            if value:return value
            time.sleep(.02)
        raise AssertionError('Timeout: '+label)
    with tempfile.TemporaryDirectory(dir=ROOT/'test-output',prefix='sequence-v093-') as scratch:
        root=Path(scratch);shutil.copytree(ROOT/'config',root/'config');shutil.copytree(ROOT/'web',root/'web');seed_send_monitor(root)
        for name in ['continuous-rules.json','tc-bindings.json','observed-commands.json','sequences.json']:(root/'config'/name).unlink(missing_ok=True)
        fields=[dict(Id='测试/A1',Code='A1',Name='测试值',Group='测试',Offset=0,Length=2,Type='uint',Scale=1,Supported=True),dict(Id='测试/CCYK00001',Code='CCYK00001',Name='船时',Group='测试',Offset=2,Length=8,Type='uint',Scale=1,Precision=0,Supported=True)]
        packets=[dict(Id='source-e',Name='TME包',OuterSourceId=0x0802,SourceIP='127.0.0.1'),dict(Id='source-b',Name='TMB包',OuterSourceId=0x0202,SourceIP='127.0.0.2')]
        for p in packets:p.update(Enabled=True,MappingConfirmed=True,Apid=8,ExpectedLengthField=-1,Groups=['测试'])
        (root/'config/decoder.json').write_text(json.dumps(dict(SchemaVersion=2,Revision='v093-fixture',Catalog=dict(Parameters=fields,Sheets=[dict(Name='测试',Index=2)]),Packets=packets)),encoding='utf-8')
        udp_port=free_port(socket.SOCK_DGRAM);web_port=free_port();settings=json.loads((root/'config/server.json').read_text(encoding='utf-8-sig'));settings.update(BindAddress='127.0.0.1',UdpPort=udp_port,SourceIPs=['127.0.0.1','127.0.0.2'],MulticastGroup='',MulticastInterface='',DataTimeoutMs=500);(root/'config/server.json').write_text(json.dumps(settings),encoding='utf-8')
        (root/'说明').mkdir();(root/'说明/使用说明.md').write_text('new manual',encoding='utf-8');(root/'使用说明.md').write_text('original manual',encoding='utf-8');(root/'自定义说明.md').write_text('custom manual',encoding='utf-8')
        c=Client(web_port);proc=None;seq={'e':0,'b':0};sockets={}
        for name,ip in [('e','127.0.0.1'),('b','127.0.0.2')]:sockets[name]=socket.socket(socket.AF_INET,socket.SOCK_DGRAM);sockets[name].bind((ip,0))
        def boot():
            nonlocal proc
            proc=subprocess.Popen([str(exe),'--local','--root',str(root)+'\\','--web-port',str(web_port)],stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL,creationflags=subprocess.CREATE_NO_WINDOW)
            def ready():
                try:c.call('/api/auth/me');return True
                except OSError:return False
            until(ready,'server')
        def preview(rows,mode='judge'):
            raw=xlsx([('任务',rows)]);url=c.base+'/api/sequence/preview?filename=test.xlsx&mode='+mode+'&timeout=2&sourceIP=127.0.0.1'
            return json.load(c.opener.open(urllib.request.Request(url,data=raw,headers={'Content-Type':'application/octet-stream','X-CSRF-Token':c.csrf})))
        def install(rows,mode='judge'):
            value=preview(rows,mode);item=value['definitions'][0];assert not item['errors'],item['errors'];c.call('/api/sequence/apply',dict(token=value['token'],ids=[item['definition']['Id']],mode='replace'));return item['definition']['Id']
        def start(id,**extra):
            cfg=c.call('/api/sequence/config');return c.call('/api/sequence/control',dict(action='start',id=id,runName='V093测试',revision=cfg['revision'],requestId=secrets.token_hex(16),**extra))['runId']
        def state(run):return c.call('/api/sequence/state?runId='+run)
        def done(run):until(lambda:not state(run)['runtime']['running'],'sequence completed');return state(run)
        def control(run,action):
            info=state(run)['runtime'];return c.call('/api/sequence/control',dict(action=action,runId=run,controlRevision=info['controlRevision']))
        def send(name,ship,value=1,duplicate=False,corrupt=False):
            if not duplicate:seq[name]+=1
            b=bytearray(34);b[0:2]=(0x0802 if name=='e' else 0x0202).to_bytes(2,'big');b[4:6]=(27).to_bytes(2,'big');b[6:8]=bytes.fromhex('0808');b[8:10]=(0xc000|seq[name]).to_bytes(2,'big');b[10:12]=(21).to_bytes(2,'big');b[12:16]=bytes([0x30,3,25,0]);b[18:20]=(0x1004).to_bytes(2,'big');b[22:24]=value.to_bytes(2,'big');b[24:32]=ship.to_bytes(8,'big');a=z=0
            for v in b[6:-2]:a=(a+v)%255;z=(z+a)%255
            b[-2]=(255-(a+z)%255)%255;b[-1]=(255-(a+b[-2])%255)%255
            if corrupt:b[-1]^=1
            before=c.call('/api/state')['received'];sockets[name].sendto(b,('127.0.0.1',udp_port));until(lambda:c.call('/api/state')['received']>before,'UDP received')
        def ship_snapshot():
            id=install([['TEXT','记录最新船时']]);return done(start(id))['rows'][0]['shipTime']
        try:
            boot();check(not list(root.glob('*.md')) and (root/'说明/自定义说明.md').read_text()=='custom manual','startup moves legacy root Markdown into the documentation folder')
            check((root/'说明/使用说明.md').read_text()=='new manual' and (root/'说明/历史根目录文件/使用说明.md').read_text()=='original manual','documentation name conflicts preserve both the current and original manuals')
            password=secrets.token_urlsafe(18);c.call('/api/auth/setup',dict(username='v093qa',name='V093 QA',password=password));c.csrf=c.call('/api/auth/login',dict(username='v093qa',password=password))['csrf'];c.call('/api/control',dict(action='start'))
            for mode in ['judge','execute']:
                id=install([['TEXT','F时刻建立T0','','','T0','F:.3'],['TEXT','等T0','','','','T0:.2'],['TEXT','相对延时','','','','.15']],mode);run=start(id);s=state(run)
                check(s['runtime']['t0'] is None and s['rows'][0]['status']=='等待计划时间',mode+' TEXT waits for F instead of completing on arrival')
                s=done(run);r=s['rows'];check(all(x['status']=='通过' and x['actual']+.005>=x['planned'] for x in r),mode+' TEXT rows pass only after all scheduled times')
                check(.28<=s['runtime']['t0']<.6 and abs(r[1]['planned']-s['runtime']['t0']-.2)<.02 and r[2]['actual']-r[1]['actual']>=.14,mode+' TEXT establishes T0 on completion and honors relative delays')
            id=install([['TEXT','稍后到达','','','','F:.25'],['TEXT','错过计划立即执行','','','','F:.05']]);s=done(start(id));check(s['rows'][1]['delay']>=.18 and abs(s['rows'][1]['actual']-s['rows'][0]['actual'])<.08,'a late TEXT records delay and executes once on actual arrival')
            id=install([['TEXT','无时间立即通过']],'execute');s=done(start(id));check(s['rows'][0]['status']=='通过' and s['rows'][0]['actual']<.15,'blank TEXT stays immediate rather than inheriting the execution one-second default')
            for expected in ['bad','-1','T0:.2']:
                check(bool(preview([['TEXT','无效计时','','','',expected]])['definitions'][0]['errors']),'TEXT rejects invalid or unestablished timing '+expected)
            check(bool(preview([['TEXT','不能自引用','','','T0','T0:.2']])['definitions'][0]['errors']),'a TEXT marker cannot use its own not-yet-established T0')
            id=install([['PAR','并行开始','','','','0'],['TEXT','并行定时说明','','','','F:.35'],['','参数','','TME.A1.value==1','','0'],['CHANGE','并行结束','','','','0']]);run=start(id);until(lambda:any(r['input']['Row']==4 and r['status']=='等待参数' for r in state(run)['rows']),'parallel predicate');send('e',1);s=state(run);check(s['runtime']['running'] and s['rows'][1]['status']=='等待计划时间','a parallel TEXT remains pending after other parallel work has passed');s=done(run);check(s['rows'][1]['actual']>=.33,'parallel completion waits for the scheduled TEXT')
            run=start(id,startRow='3');until(lambda:any(r['input']['Row']==4 and r['status']=='等待参数' for r in state(run)['rows']),'parallel entry predicate');send('e',2);s=state(run);check(s['rows'][0]['status']=='已跳过' and s['rows'][1]['status']=='等待计划时间','starting inside a parallel segment still schedules its TEXT');check(done(run)['rows'][1]['actual']>=.33,'parallel entry cannot bypass the TEXT planned time')
            id=install([['TEXT','暂停冻结','','','T0','F:.45']],'execute');run=start(id);time.sleep(.06);control(run,'pause');elapsed=state(run)['runtime']['elapsed'];time.sleep(.25);s=state(run);check(s['runtime']['t0'] is None and abs(s['runtime']['elapsed']-elapsed)<.04,'manual pause freezes TEXT timing and postpones T0');control(run,'resume');s=done(run);check(s['rows'][0]['actual']>=.43,'resumed TEXT completes against the frozen sequence clock')
            id=install([['TEXT','原始T0','','','T0'],['TEXT','中途启动','','','','T0:.5']]);run=start(id,startRow='3',t0Elapsed='.4',f0Elapsed='1');s=done(run);check(s['rows'][0]['status']=='已跳过' and 1.08<=s['rows'][1]['actual']<1.4,'TEXT scheduling respects start-row and preset T0/F0')
            exact=9007199254740993;send('e',exact);snapshot=ship_snapshot();check(snapshot['Status']=='有效' and snapshot['Value']==str(exact) and snapshot['SourceIP']=='127.0.0.1','TME ship time retains exact uint64 text')
            send('b',7);snapshot=ship_snapshot();check(snapshot['Status']=='有效' and snapshot['Value']=='7' and snapshot['SourceIP']=='127.0.0.2','newer TMB CCYK00001 wins even with a smaller value and another source IP')
            send('e',11);snapshot=ship_snapshot();check(snapshot['Value']=='11' and snapshot['SourceIP']=='127.0.0.1','a later TME packet takes over ship time again')
            send('b',999,duplicate=True);check(ship_snapshot()['Value']=='11','a duplicate SPP does not replace the latest ship time');send('b',888,corrupt=True);check(ship_snapshot()['Value']=='11','a bad-checksum SPP does not replace the latest ship time')
            send('b',123);id=install([['TEXT','固定船时快照']]);run=start(id);s=done(run);send('e',456);check(state(run)['rows'][0]['shipTime']['Value']=='123','new telemetry does not rewrite a completed row ship-time snapshot')
            events=c.call('/api/sequence/history?runId='+run)['rows'];check(next(e for e in events if e['Event']=='步骤结果')['ShipTime']['Value']=='123','archived step history keeps the selected ship time')
            time.sleep(.55);check(ship_snapshot()['Status']=='数据过期','expired latest ship time remains explicitly unavailable')
            c.call('/api/control',dict(action='stop'));report=ROOT/'test-output/release-v0.9.3/integration-report.json';report.write_text(json.dumps(dict(checks=len(checks),details=checks),ensure_ascii=False,indent=2),encoding='utf-8');print('All',len(checks),'V0.9.3 integration checks passed. Loopback only.')
        finally:
            for sock in sockets.values():sock.close()
            if proc and proc.poll() is None:proc.terminate();proc.wait(timeout=8)

if __name__=='__main__':main()
