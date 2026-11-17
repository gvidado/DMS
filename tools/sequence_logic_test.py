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
            start_server();password=secrets.token_urlsafe(18);c.call('/api/auth/setup',dict(username='logicqa',name='Logic QA',password=password));c.csrf=c.call('/api/auth/login',dict(username='logicqa',password=password))['csrf'];c.call('/api/control',dict(action='start'))
            invalid=['TM.A1.value && TM.A2.value > 0','1 < TM.A1.value < 3','abs(TM.A1.value > 0) > 0','(TM.A1.value == 1) + 1 > 0','TM.A1.value + 1','TM.A1.value > 0 OR','TM.A1.value > 0 & TM.A2.value > 0','RET=Normal','this.value > 0','TC.K001 OR TM.A1.value > 0']
            for expression in invalid:
                check(bool(preview([['','非法条件','',expression]])['definitions'][0]['errors']),'invalid logic or forbidden syntax is rejected: '+expression)
            install([['','与条件','','TM.A1.value == 1 && TM.A2.value == 2']]);start();data(1,0);time.sleep(.1);check(state()['runtime']['running'],'AND does not pass until both parameter comparisons are true');data(1,2);done();check(state()['runtime']['abnormal']==0,'AND passes with both values satisfied');check(state()['rows'][0]['status']=='通过' and state()['rows'][0]['reason']=='','passing a previously false condition clears its waiting reason');events=c.call('/api/sequence/history?runId='+state()['runtime']['runId'])['rows'];check(all(e['Reason']=='' for e in events if e['Event']=='步骤结果' and e['Status']=='通过'),'new persisted passed steps have no obsolete waiting reason')
            install([['','或条件','','TM.A1.value == 1 || TM.A2.value == 2']]);start();data(0,0);time.sleep(.1);check(state()['runtime']['running'],'OR stays pending when both branches are false');data(0,2);done();check(state()['runtime']['abnormal']==0,'OR passes when either parameter branch is true')
            install([['','优先级','','TM.A1.value == 1 || TM.A2.value == 2 && TM.A1.value == 3']]);start();data(0,2);time.sleep(.1);check(state()['runtime']['running'],'AND binds more tightly than OR');data(1,0);done()
            cases=[('!(TM.A1.value == 1) && INENUM(TM.A2.value)',2,0),('NOT(TM.A1.value == 1) AND INENUM(TM.A2.value)',2,0),('not(TM.A1.value == 1) and (TM.A2.value == 0 or TM.A2.value == 2)',2,2),('非(TM.A1.value == 1) 与 (TM.A2.value == 0 或 TM.A2.value == 2)',2,0),('abs(TM.A1.value - 2) <= 0.1 && (TM.A2.value == 0 || TM.A2.value == 2)',2,0),('(TM.A1.value == 1 || TM.A2.value == 2) && TM.A1.value == 3',3,2),('INENUM(TM.A2.value) == true && TM.A1.value != 1',2,0),('TM.A1.value == 1 || 1 / 0 > 1',1,0)]
            for expression,value,enum in cases:
                install([['','组合判据','',expression]]);start();data(value,enum);done();check(state()['runtime']['abnormal']==0,'logical formula evaluates correctly: '+expression)
            install([['','短路与','','TM.A1.value == 1 && 1 / 0 > 1']],timeout=2);start();data(0);time.sleep(.1);check(state()['runtime']['running'] and state()['runtime']['abnormal']==0,'false AND short-circuits division by zero without an expression error');control('stop')
            install([['PAR','并行'],['HOLD','持续逻辑','','TM.A1.value >= 1 && (TM.A2.value == 0 || TM.A2.value == 2)'],['','结束','','TM.A1.value == 5 && TM.A2.value == 0'],['CHANGE','结束']]);start();data(0,1);row_status(3,'异常');data(2,0);row_status(3,'持续检查（曾异常）');data(5,0);done();check(state()['runtime']['abnormal']==1,'HOLD uses the complete logical expression and retains the abnormal episode after recovery');check(state()['rows'][1]['status']=='异常' and bool(state()['rows'][1]['reason']),'a recovered HOLD retains abnormal status and its reason')
            install([['','发出后判读','K001','TM.A1.value == 1 && TM.A2.value == 2','','.2'],['','下一指令','K002','','','.2']],'execute',2);start();received(1);data(1,0);time.sleep(.1);empty();check(state()['runtime']['running'],'execution waits for its logical condition after sending');data(1,2);received(2);done();check(state()['runtime']['abnormal']==0,'execution advances and sends the next command only after the full logical condition passes');check(state()['rows'][0]['reason']=='','execution also clears stale waiting reasons after a condition passes')
        finally:
            if proc and proc.poll() is None:proc.terminate();proc.wait(timeout=6)
            receiver.close();sender.close()
    print('All',len(checks),'sequence logical-expression checks passed. Test traffic used loopback only.')
if __name__=='__main__':main()
