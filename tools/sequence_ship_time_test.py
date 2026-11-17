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
        parameters=json.loads((root/'config/parameters.json').read_text(encoding='utf-8-sig'));parameters=[p for p in parameters if p.get('Code')!='CCYK00001'];ship=dict(parameters[0]);ship.update(Id='ship-time',Code='CCYK00001',Name='船时',Offset=8,Length=8,Type='uint',Scale=1,Bias=0,Precision=0,Enums={},BitStart=-1);parameters.append(ship);(root/'config/parameters.json').write_text(json.dumps(parameters),encoding='utf-8');settings['DataTimeoutMs']=1000;(root/'config/server.json').write_text(json.dumps(settings),encoding='utf-8')
        def shipdata(value,shiptime):
            nonlocal seq
            seq+=1;raw=packet(seq);raw[22:27]=round(value*10000).to_bytes(5,'big');raw[30:38]=shiptime.to_bytes(8,'big');sender.sendto(seal(raw),('127.0.0.1',receive_port))
        try:
            start_server();password=secrets.token_urlsafe(18);c.call('/api/auth/setup',dict(username='shipqa',name='Ship QA',password=password));c.csrf=c.call('/api/auth/login',dict(username='shipqa',password=password))['csrf'];c.call('/api/control',dict(action='start'))
            install([['TEXT','没有船时数据']]);start();done();check(state()['rows'][0]['shipTime']['Status']=='未接收','recording without ship telemetry gives a clear unavailable status without blocking the sequence')
            shipdata(0,111);wait(lambda:c.call('/api/state')['accepted']>=1,'ship seed');install([['','参数通过时记录','','TM.A1.value==1']]);info=start();shipdata(0,222);time.sleep(.06);exact=9007199254740993;shipdata(1,exact);done();snapshot=state()['rows'][0]['shipTime'];check(snapshot['Value']==str(exact) and snapshot['Status']=='有效' and snapshot['SourceIP']=='127.0.0.1' and bool(snapshot['SampleTime']),'completion captures CCYK00001 from the same decoded frame without losing uint64 digits')
            events=c.call('/api/sequence/history?runId='+info['runId'])['rows'];check(next(e for e in events if e['Event']=='步骤结果')['ShipTime']==snapshot,'persisted step result uses the exact row snapshot')
            shipdata(2,77);time.sleep(.06);check(state()['rows'][0]['shipTime']==snapshot,'later telemetry does not change recorded ship time')
            raw=c.opener.open(c.base+'/api/sequence/export.csv?runId='+info['runId']).read();rows=list(csv.DictReader(io.StringIO(raw.decode('utf-8-sig'))));check(any(r['事件']=='步骤结果' for r in rows) and all(len(r)==11 and '船时' not in r and '船时状态' not in r for r in rows),'concise sequence CSV excludes ship columns while detailed history retains its recorded ship time')
            time.sleep(1.05);install([['TEXT','过期船时']]);start();done();check(state()['rows'][0]['shipTime']['Value']=='' and state()['rows'][0]['shipTime']['Status']=='数据过期' and state()['runtime']['abnormal']==0,'stale ship telemetry is marked unavailable while normal sequence behavior is preserved')
            shipdata(0,456);wait(lambda:c.call('/api/state')['accepted']>=5,'execution ship seed');install([['','发送记录船时','K001','','','.2']],'execute',2);info=start();received(1);done();events=c.call('/api/sequence/history?runId='+info['runId'])['rows'];check(all(e['ShipTime']['Value']=='456' for e in events if e['Event'] in ['指令发送','步骤结果']),'outgoing command and completion records each capture their own ship snapshot')
            shipdata(0,789);time.sleep(.05);install([['','重启前运行','','TM.A1.value==99']],timeout=10);info=start();saved_run=info['runId'];proc.terminate();proc.wait(timeout=5);start_server();c=Client(web_port);c.csrf=c.call('/api/auth/login',dict(username='shipqa',password=password))['csrf'];events=c.call('/api/sequence/history?runId='+saved_run)['rows'];check(next(e for e in events if e['Event']=='结束')['ShipTime']['Status']=='重启时无实时数据','restart termination never reuses the original start-event ship value')
            proc.terminate();proc.wait(timeout=5);parameters=[p for p in parameters if p['Code']!='CCYK00001'];(root/'config/parameters.json').write_text(json.dumps(parameters),encoding='utf-8');start_server();c=Client(web_port);c.csrf=c.call('/api/auth/login',dict(username='shipqa',password=password))['csrf'];c.call('/api/control',dict(action='start'));install([['TEXT','缺失船时参数']]);start();done();check(state()['rows'][0]['shipTime']['Status']=='未配置CCYK00001','missing ship-time configuration is explicit and does not prevent execution')
            old=c.call('/api/sequence/history?runId='+saved_run)['rows'];check(next(e for e in old if e['Event']=='开始')['ShipTime']['Value']=='789','original ship snapshots remain intact across restarts and catalog changes')
        finally:
            if proc and proc.poll() is None:proc.terminate();proc.wait(timeout=6)
            receiver.close();sender.close()
    print('All',len(checks),'ship-time snapshot checks passed. Traffic used loopback only.')
if __name__=='__main__':main()
