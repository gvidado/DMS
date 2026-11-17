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
            start_server();password=secrets.token_urlsafe(18);c.call('/api/auth/setup',dict(username='timingqa',name='Timing QA',password=password));c.csrf=c.call('/api/auth/login',dict(username='timingqa',password=password))['csrf'];c.call('/api/control',dict(action='start'))
            for rows in [[['','无基准','','TM.A1.value==1','','T0:1']],[['','错误标志','','TM.A1.value==1','OTHER','1']]]:
                check(bool(preview(rows)['definitions'][0]['errors']),'judge rejects invalid or missing time anchors')
            install([['','计划后才判读','','TM.A1.value==1','','.6','.15']]);start();data(1);time.sleep(.2);check(state()['rows'][0]['status']=='等待计划时间' and state()['runtime']['running'],'early data cannot pass a scheduled judgment and planned waiting does not consume its timeout');row_status(2,'等待参数');data(1);done();check(state()['rows'][0]['actual']>=.6 and state()['runtime']['abnormal']==0,'fresh data after the scheduled start passes within the actual-start timeout')
            install([['','建立基准','','TM.A1.value==1','T0','.15'],['','按T0判读','','TM.A1.value==2','','T0:.35']],timeout=1);start();row_status(2,'等待参数');data(1);wait(lambda:state()['runtime']['t0'] is not None,'T0 established');anchor=state()['runtime']['t0'];info=state()['runtime'];check(bool(info['t0EstablishedAt']) and abs(info['elapsed']-info['t0']-info['t0Elapsed'])<.001,'T0 exposes its actual establishment timestamp and elapsed time consistently');data(2);time.sleep(.1);check(state()['runtime']['running'] and state()['rows'][1]['status']=='等待计划时间','T0 is established at marker completion and early next-row data is ignored');row_status(3,'等待参数');data(2);done();check(abs(state()['rows'][1]['planned']-anchor-.35)<.02,'judge T0:n schedules from its own completed marker')
            install([['','相对基准','','TM.A1.value==1','T0'],['','相对延迟','','TM.A1.value==2','','.3']],timeout=1);start();row_status(2,'等待参数');data(1);wait(lambda:state()['runtime']['t0'] is not None,'relative baseline');anchor=state()['runtime']['t0'];check(abs(state()['rows'][1]['planned']-anchor-.3)<.02,'numeric expected time is relative to the previous row completion');row_status(3,'等待参数');data(2);done()
            install([['','延迟监视','K001','','','.6']],timeout=1);start();command(1);time.sleep(.1);check(state()['rows'][0]['status']=='等待计划时间','a command arriving before the schedule does not satisfy the step');row_status(2,'等待指令');time.sleep(.05);check(state()['runtime']['running'],'the old command remains unusable after scheduled activation');command(1);done();check(c.call('/api/command-send/history')['total']==0,'judge waits for a new captured command after due time and never actively sends')
            install([['PAR','并行开始'],['HOLD','延迟持续','','TM.A1.value>=1','','.15'],['','延迟完成','','TM.A1.value==5','','.35'],['CHANGE','结束']],timeout=1);start();data(0);row_status(3,'持续检查');data(1);row_status(4,'等待参数');data(5);done();check(state()['runtime']['abnormal']==0,'parallel and HOLD checks honor each row due time, including data before HOLD starts')
            def add_sheet(name,rows):
                p=preview([],timeout=1,sheets=[(name,rows)]);item=p['definitions'][0];assert not item['errors'],item['errors'];c.call('/api/sequence/apply',dict(token=p['token'],ids=[item['definition']['Id']],mode='add'));return next(d for d in c.call('/api/sequence/config')['definitions'] if d['Id']==item['definition']['Id'])
            target=add_sheet('目标判读',[['','新基准','','TM.A1.value==2','T0'],['','新T0判读','','TM.A1.value==3','','T0:.2']]);source=add_sheet('来源判读',[['','旧基准','','TM.A1.value==1','T0'],['CALL','计划跳转',target['Code'],'','','T0:.2']]);current=source['Id'];start();row_status(2,'等待参数');data(1);wait(lambda:state()['runtime']['id']==target['Id'],'scheduled passive CALL');current=target['Id'];check(state()['runtime']['t0'] is None and state()['runtime']['elapsed']<.15,'scheduled judge CALL resets the target elapsed time and T0');check(state()['runtime']['t0EstablishedAt']=='' and state()['runtime']['t0Elapsed'] is None,'CALL clears all T0 display metadata before the target establishes its own marker');row_status(2,'等待参数');data(2);wait(lambda:state()['runtime']['t0'] is not None,'target marker');row_status(3,'等待参数');data(3);done();check(state()['runtime']['abnormal']==0,'jumped-to judge sequence rebuilds its own T0 and completes on schedule')
        finally:
            if proc and proc.poll() is None:proc.terminate();proc.wait(timeout=6)
            receiver.close();sender.close()
    print('All',len(checks),'passive judgment timing checks passed. Traffic used loopback only.')
if __name__=='__main__':main()
