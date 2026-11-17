"""Sequence integration against an isolated server and real loopback UDP/Npcap only."""
import csv,io,json,secrets,shutil,socket,subprocess,sys,tempfile,time,urllib.request,zipfile
from pathlib import Path
from xml.sax.saxutils import escape
from integration_test import ROOT,Client,free_port,wait,packet,seal,seed_send_monitor

HEADERS=['关键字','测试工作程序内容及要求','指令','判读要求','标志','期望执行时间','超时时间']
def xlsx(sheets):
    out=io.BytesIO()
    with zipfile.ZipFile(out,'w',zipfile.ZIP_DEFLATED) as z:
        z.writestr('[Content_Types].xml','<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"/>')
        z.writestr('xl/workbook.xml','<workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets>'+''.join(f'<sheet name="{escape(name)}" sheetId="{i}" r:id="r{i}"/>' for i,(name,_) in enumerate(sheets,1))+'</sheets></workbook>')
        z.writestr('xl/_rels/workbook.xml.rels','<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">'+''.join(f'<Relationship Id="r{i}" Target="worksheets/sheet{i}.xml"/>' for i in range(1,len(sheets)+1))+'</Relationships>')
        for i,(_,rows) in enumerate(sheets,1):
            xml=[]
            for r,values in enumerate([HEADERS]+rows,1):
                xml.append(f'<row r="{r}">'+''.join(f'<c r="{chr(65+c)}{r}" t="inlineStr"><is><t>{escape(str(v))}</t></is></c>' for c,v in enumerate(values) if v is not None)+'</row>')
            z.writestr(f'xl/worksheets/sheet{i}.xml','<worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData>'+''.join(xml)+'</sheetData></worksheet>')
    return out.getvalue()

def main():
    exe=Path(sys.argv[1]).resolve();checks=[]
    def check(ok,name):assert ok,name;checks.append(name);print('PASS',name,flush=True)
    with tempfile.TemporaryDirectory(prefix='sequence-http-',dir=ROOT/'test-output') as temp:
        root=Path(temp);shutil.copytree(ROOT/'config',root/'config');shutil.copytree(ROOT/'web',root/'web')
        for name in ['decoder.json','continuous-rules.json','tc-bindings.json','command-send.json','sequences.json']:(root/'config'/name).unlink(missing_ok=True)
        seed_send_monitor(root);receive_port=free_port(socket.SOCK_DGRAM);source_port=free_port(socket.SOCK_DGRAM);web_port=free_port()
        settings=json.loads((root/'config/server.json').read_text(encoding='utf-8-sig'));settings.update(BindAddress='127.0.0.1',UdpPort=receive_port,SourceIPs=['127.0.0.1'],MappingConfirmed=True,MulticastGroup='',MulticastInterface='',DataTimeoutMs=30000);(root/'config/server.json').write_text(json.dumps(settings),encoding='utf-8')
        commands=[dict(Number=i,Name=f'测试指令{i}',DestinationIP='127.0.0.2',DestinationIPs=['127.0.0.2'],PayloadHex=f'AA BB {i:02X}') for i in [1,2]]
        (root/'config/observed-commands.json').write_text(json.dumps(dict(SchemaVersion=1,SourceIP='127.0.0.1',IdentityRule='udp-payload',NextNumber=3,Commands=commands)),encoding='utf-8')
        (root/'config/tc-bindings.json').write_text(json.dumps(dict(SchemaVersion=1,Revision='fixture',Bindings=[dict(Code=f'K00{i}',Number=i) for i in [1,2]])),encoding='utf-8')
        receiver=socket.socket(socket.AF_INET,socket.SOCK_DGRAM);receiver.bind(('127.0.0.2',0));receiver.settimeout(.12)
        (root/'config/command-send.json').write_text(json.dumps(dict(SourceIP='127.0.0.1',Routes=[dict(DestinationIP='127.0.0.2',SourcePort=source_port,DestinationPort=receiver.getsockname()[1])])),encoding='utf-8')
        sender=socket.socket(socket.AF_INET,socket.SOCK_DGRAM);sender.bind(('127.0.0.1',0));seq=0;proc=None;c=Client(web_port);current=None;start_body=None
        def start_server():
            nonlocal proc
            proc=subprocess.Popen([str(exe),'--local','--root',str(root),'--web-port',str(web_port)],stdout=subprocess.DEVNULL,stderr=subprocess.STDOUT,creationflags=getattr(subprocess,'CREATE_NO_WINDOW',0))
            def ready():
                try:c.call('/api/auth/me');return True
                except OSError:return False
            wait(ready,'sequence server')
        def preview(rows,mode='judge',timeout=.8,sheets=None):
            data=xlsx(sheets or [('任务',rows)]);url=c.base+f'/api/sequence/preview?filename=fixture.xlsx&mode={mode}&timeout={timeout}&sourceIP=127.0.0.1'
            request=urllib.request.Request(url,data=data,headers={'Content-Type':'application/octet-stream','X-CSRF-Token':c.csrf});return json.loads(c.opener.open(request).read())
        def install(rows,mode='judge',timeout=.8):
            nonlocal current
            p=preview(rows,mode,timeout);assert not p['definitions'][0]['errors'],p['definitions'][0]['errors'];current=p['definitions'][0]['definition']['Id'];c.call('/api/sequence/apply',dict(token=p['token'],ids=[current],mode='replace'));return current
        def state():return c.call('/api/sequence/state?id='+current)
        def start(expected=200):
            nonlocal start_body
            cfg=c.call('/api/sequence/config');start_body=dict(action='start',runName='任务',id=current,requestId=secrets.token_hex(16),revision=cfg['revision']);return c.call('/api/sequence/control',start_body,expected=expected)
        def done():wait(lambda:not state()['runtime']['running'],'sequence completed')
        def row_status(number,status):wait(lambda:any(r['input']['Row']==number and r['status']==status for r in state()['rows']),f'row {number} {status}')
        def data(value,enum=0,duplicate=False):
            nonlocal seq
            if not duplicate:seq+=1
            raw=packet(seq);raw[22:27]=round(value*10000).to_bytes(5,'big');raw[27:30]=enum.to_bytes(3,'big');sender.sendto(seal(raw),('127.0.0.1',receive_port))
        def command(number):
            sender.sendto(bytes([0xaa,0xbb,number]),receiver.getsockname());value,_=receiver.recvfrom(65535);assert value==bytes([0xaa,0xbb,number])
        def received(number):
            deadline=time.monotonic()+3
            while True:
                try:raw,source=receiver.recvfrom(65535);assert raw==bytes([0xaa,0xbb,number]) and source[1]==source_port;return time.monotonic()
                except socket.timeout:
                    if time.monotonic()>deadline:raise
        def empty():
            try:receiver.recvfrom(65535);raise AssertionError('unexpected active send')
            except socket.timeout:pass
        def control(action,info=None):
            info=info or state()['runtime'];return c.call('/api/sequence/control',dict(action=action,runId=info['runId'],controlRevision=info['controlRevision']))
        try:
            start_server();c.call('/api/sequence/config',expected=401);password=secrets.token_urlsafe(18);c.call('/api/auth/setup',dict(username='sequence_qa',name='序列测试',password=password));c.csrf=c.call('/api/auth/login',dict(username='sequence_qa',password=password))['csrf']
            c.call('/api/sequence/config',dict(defaultTimeout=.8,revision='bad'),csrf=False,expected=403);check(True,'sequence APIs require login and CSRF')
            bad=preview([['CHANGE','孤立边界'],['HOLD','段外持续','','TM.A1.value>0'],['PAUSE','仅执行可暂停'],['','旧引用','COMD.X'],['','无绑定this','','this.value==1'],['','未知参数','','TM.MISSING.value==0']]);check(len(bad['definitions'][0]['errors'])==6,'all invalid structure, mode, old command, this and missing-parameter rows are reported')
            p=preview([],sheets=[('正常',[['','枚举','','INENUM(TM.A2.value)']]),('异常',[['HOLD','段外','','TM.A1.value>0']])]);ids=[d['definition']['Id'] for d in p['definitions']];before=c.call('/api/sequence/config');c.call('/api/sequence/apply',dict(token=p['token'],ids=ids,mode='replace'),expected=400);check(c.call('/api/sequence/config')['revision']==before['revision'],'multi-sheet import fails atomically when a selected sheet contains errors')
            c.call('/api/sequence/apply',dict(token=p['token'],ids=[ids[0]],mode='replace'));check(len(c.call('/api/sequence/config')['definitions'])==1,'explicitly selected valid sheet can be saved without silently running invalid sheets')
            check(bool(preview([['','错误时标','','TM.A1.value==1','nonsense','not a time']])['definitions'][0]['errors']),'judge validates marker and expected time instead of ignoring invalid text');install([['','等待数据','','TM.A1.value==1']]);start(expected=409);check(True,'start checks listener')
            c.call('/api/control',dict(action='start'))
            rules=c.call('/api/continuous-rules');parameter=next(p for p in c.call('/api/catalog') if p['Code']=='A1');rule=dict(Code='A1',Name='独立常态化',Level='一般',ParameterId=parameter['Id'],SourceIP='127.0.0.1',Enabled=True,Script='RET=Normal;');c.call('/api/continuous-rules/save',dict(revision=rules['revision'],rule=rule));c.call('/api/continuous-rules/control',dict(action='start'))
            data(0);wait(lambda:c.call('/api/state')['accepted']>=1,'seed data');install([['','必须有新帧','','TM.A1.value==0']]);start();data(0,duplicate=True);time.sleep(.08);check(state()['runtime']['running'],'cached value and duplicate frame cannot immediately pass a new step');data(0);done()
            install([['','串行','','TM.A1.value==1','',''],['PAR','开始并行','','','','','.08'],['','先满足','','TM.A1.value==2','','','.5'],['','后满足','','TM.A1.value==3','','','.7'],['HOLD','持续正常','','TM.A1.value<10','','','.7'],['','并行监视','K001','','','','.8'],['CHANGE','结束'],['','后续串行','','abs(TM.A1.value-4)<=0.01']]);start();row_status(2,'等待参数');data(1);row_status(4,'等待参数');data(2);row_status(4,'通过');time.sleep(.13);data(3);row_status(5,'通过');check(state()['runtime']['running'],'parallel ordinary conditions latch independently and PAR timeout is not group deadline');command(1);row_status(9,'等待参数');data(4);done();check(state()['runtime']['abnormal']==0 and c.call('/api/command-send/history')['total']==0,'judge serial/parallel/HOLD/command monitoring completes without active UDP sends')
            install([['','先等待参数','','TM.A1.value==1'],['','再等待新指令','K002']]);start();command(2);time.sleep(.1);data(1);row_status(3,'等待指令');time.sleep(.08);check(state()['runtime']['running'],'old command event cannot satisfy a later serial step');command(2);done()
            install([['','故意超时','','TM.A1.value==99','','','.12'],['','继续判读','','TM.A1.value==4']]);start();row_status(2,'超时');row_status(3,'等待参数');data(4);done();check(state()['runtime']['abnormal']==1,'judge timeout is retained and next serial item proceeds')
            install([['PAR','并行'],['HOLD','持续','','TM.A1.value>=1'],['','结束依据','','TM.A1.value==5'],['CHANGE','结束']]);info=start();row_status(3,'持续检查');data(0);row_status(3,'异常');data(2);row_status(3,'持续检查（曾异常）');data(0);row_status(3,'异常');data(5);done();events=c.call('/api/sequence/history?runId='+info['runId'])['rows'];check(sum(e['Event']=='异常' and e['Row']==3 for e in events)==2 and state()['runtime']['abnormal']==1,'HOLD fails on first false frame, retains failure after recovery and records each new abnormal episode')
            p=preview([['PAR','开始'],['HOLD','无结束依据','','TM.A1.value>0'],['CHANGE','结束']]);check(any('完成条件' in e for e in p['definitions'][0]['errors']),'HOLD-only parallel group is rejected instead of waiting forever')
            timing=preview([['','最短间隔','K001','','','.05']],'execute');check(any('0.2秒' in w for w in timing['definitions'][0]['warnings']),'preview reports normalization of a subminimum relative interval')
            install([['','默认首次','K001'],['','最短间隔','K002','','','.05'],['','默认下一次','K001']],'execute',2);began=time.monotonic();start();first=received(1);second=received(2);third=received(1);done();check(first-began>=.95 and second-first>=.195 and third-second>=.95,'blank expected times wait one second and smaller explicit intervals execute at least 0.2 seconds apart')
            install([['','建立基准','K001','','T0','.2'],['','T0零偏移','K002','','','T0:0']],'execute',2);start();first=received(1);second=received(2);done();check(second-first>=.195 and state()['rows'][1]['delay']>=.195,'an overdue or zero-offset T0 action preserves its planned time but cannot bypass the minimum interval')
            install([['PAR','并行开始','','','','.2'],['','并行一','','TM.A1.value==7','','.05'],['','并行二','','TM.A1.value==8','','0'],['CHANGE','并行结束','','','','.2']],'execute',2);start();row_status(3,'等待参数');row_status(4,'等待参数');parallel=state()['rows'];check(abs(parallel[1]['actual']-parallel[2]['actual'])<.05,'minimum delay does not serialize parallel parameter checks');data(7);data(8);done();empty()
            install([['PAR','边界发送','K001'],['','必须等待另一条新指令','K001'],['CHANGE','结束']],'execute',1);start();received(1);row_status(3,'等待指令');time.sleep(.1);check(state()['runtime']['running'],'delayed capture of PAR send cannot satisfy a later parallel monitor of the same command');command(1);done()
            install([['','发送并等待','K001','TM.A1.value==1','T0'],['','相对T0发送','K002','','','T0:.25'],['PAUSE','人工继续'],['','继续后延时发送','K001','','','.3']],'execute',2);start();received(1);time.sleep(.3);empty();check(state()['runtime']['t0'] is None,'T0 is not established by sending before same-row parameter condition passes');data(1);t=time.monotonic();received(2);check(time.monotonic()-t>=.20,'active sending uses T0 established by complete row success');wait(lambda:state()['runtime']['paused'],'PAUSE reached');paused=state()['runtime'];time.sleep(.25);after=state()['runtime'];check(abs(after['elapsed']-paused['elapsed'])<.02 and after['t0']==paused['t0'] and after['t0EstablishedAt']==paused['t0EstablishedAt'] and abs(after['t0Elapsed']-paused['t0Elapsed'])<.02,'PAUSE freezes elapsed time and T0 scheduling');control('resume',after);resume=time.monotonic();received(1);done();check(time.monotonic()-resume>=.24,'resume preserves remaining relative delay and sends once');empty()
            install([['','不满足','','TM.A1.value==99','','','.12'],['','不能发送','K001']],'execute');start();done();empty();check(state()['runtime']['abnormal']==1,'execute timeout terminates before subsequent instruction')
            install([['PAR','开始'],['HOLD','必须持续','','TM.A1.value>0'],['','结束条件','','TM.A1.value==5'],['CHANGE','结束'],['','不能发送','K001']],'execute',1);start();row_status(3,'持续检查');data(0);done();empty();check(state()['runtime']['abnormal']==1,'execute HOLD first false frame immediately stops the entire sequence')
            install([['','定时开始后再计超时','','TM.A1.value==1','','.25','.15']],'execute');start();time.sleep(.18);check(state()['runtime']['running'],'planned delay does not consume judgement timeout');row_status(2,'等待参数');data(1);done()
            install([['','建立T0','','TM.A1.value==1','T0'],['','故意晚完成','','TM.A1.value==2'],['','迟到只发一次','K001','','','T0:.1']],'execute',2);start();row_status(2,'等待参数');data(1);row_status(3,'等待参数');time.sleep(.22);data(2);received(1);done();rows=state()['rows'];check(rows[-1]['delay']>=.10,'overdue T0 step executes immediately once and records positive lateness');same=c.call('/api/sequence/control',start_body);check(same['runId']==state()['runtime']['runId'],'replayed start request returns the existing run, even after completion');empty()
            install([['PAUSE','第一暂停'],['PAUSE','第二暂停'],['','人工两次继续后发送','K001']],'execute');start();old=state()['runtime'];control('resume',old);wait(lambda:state()['runtime']['paused'] and state()['runtime']['currentRow']==3,'second pause');c.call('/api/sequence/control',dict(action='resume',runId=old['runId'],controlRevision=old['controlRevision']),expected=409);empty();control('resume');received(1);done();check(True,'stale duplicate Continue cannot skip a later PAUSE or resend a step')
            combined=c.call('/api/abnormal-records?pageSize=100')['records'];check(all(any(r['type']==kind and r['runId'] and r['code'].startswith('第') and r['rule'] and not r['released'] for r in combined) for kind in ['sequence-execute','sequence-judge']),'live execute and judge failures appear in unified history with row, rule, run and pending status')
            check(c.call('/api/continuous-rules')['execution']['running'],'sequence stop/completion does not stop continuous judgement')
            install([['','正在等待','','TM.A1.value==99']]);start();c.call('/api/control',dict(action='stop'));check(not state()['runtime']['running'],'stopping reception also stops dependent sequence')
            raw=c.opener.open(c.base+'/api/sequence/export.csv').read();records=list(csv.reader(io.StringIO(raw.decode('utf-8-sig'))));check(len(records)>50 and len(records[0])==11 and all(len(r)==11 for r in records),'persistent CSV includes only the requested eleven summary columns');check(records[0][1]=='工作名称' and all(e['Code'].startswith(('XLP','XLZ')) for e in c.call('/api/sequence/history')['rows']),'summary CSV uses work names while full history retains sequence codes')
            c.call('/api/control',dict(action='start'));install([['PAUSE','进程中断测试'],['','重启不能发送','K001']],'execute');unfinished=start()['runId'];proc.terminate();proc.wait(timeout=8);start_server();c.csrf=c.call('/api/auth/login',dict(username='sequence_qa',password=password))['csrf'];check(not c.call('/api/sequence/config')['runtime']['running'] and any(e['Event']=='结束' and '重启' in e['Reason'] for e in c.call('/api/sequence/history?runId='+unfinished)['rows']),'restart retains definitions/history, marks unfinished run and never automatically resends');empty()
        finally:
            if proc and proc.poll() is None:proc.terminate();proc.wait(timeout=8)
            sender.close();receiver.close()
    print(f'All {len(checks)} sequence integration checks passed. Test traffic used loopback only.')
if __name__=='__main__':main()
