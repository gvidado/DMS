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
            start_server();password=secrets.token_urlsafe(18);c.call('/api/auth/setup',dict(username='textqa',name='TEXT QA',password=password));c.csrf=c.call('/api/auth/login',dict(username='textqa',password=password))['csrf'];c.call('/api/control',dict(action='start'))
            for mode in ['judge','execute']:
                p=preview([['text','建立起点','','','t0'],['','稍后判读','','TM.A1.value==1','','T0:.3']],mode,2)
                check(not p['definitions'][0]['errors'],'TEXT T0 accepted in '+mode)
                install([['TEXT','建立起点','','','T0'],['','稍后判读','','TM.A1.value==1','','T0:.3']],mode,2);start();s=state();anchor=s['runtime']['t0']
                check(anchor is not None and anchor<.15 and s['rows'][0]['status']=='通过' and bool(s['runtime']['t0EstablishedAt']),'TEXT establishes T0 on arrival with timestamp in '+mode)
                data(1);time.sleep(.08);check(state()['rows'][1]['status']=='等待计划时间','pre-T0-due sample cannot pass in '+mode)
                row_status(3,'等待参数');data(1);done();s=state();check(abs(s['rows'][1]['planned']-anchor-.3)<.02 and s['runtime']['abnormal']==0,'T0 schedule completes in '+mode)
            install([['','先等待','','TM.A1.value==2','','F:.1'],['TEXT','后来建立','','','T0'],['','从新基准等待','','TM.A1.value==1','','T0:.25']],timeout=2);start();row_status(2,'等待参数');time.sleep(.15);data(2);wait(lambda:state()['runtime']['t0'] is not None,'later TEXT T0');s=state();check(s['runtime']['t0']>=.2 and abs(s['rows'][1]['actual']-s['runtime']['t0'])<.02,'TEXT uses actual arrival rather than sequence start');row_status(4,'等待参数');data(1);done()
            p=preview([['TEXT','忽略动作','K001','bad syntax','T0','F:.25','bad'],['','等待','','TM.A1.value==1','','T0:.2']],timeout=2);check(not p['definitions'][0]['errors'] and bool(p['definitions'][0]['warnings']),'TEXT action fields produce warning without changing marker semantics')
            install([['TEXT','忽略动作','K001','bad syntax','T0','F:.25','bad'],['','等待','','TM.A1.value==1','','T0:.2']],timeout=2);start();check(state()['runtime']['t0'] is None,'TEXT waits for its expected F time');wait(lambda:state()['runtime']['t0'] is not None,'timed TEXT T0');check(state()['runtime']['t0']>=.23,'TEXT establishes T0 after its F schedule');empty();row_status(3,'等待参数');data(1);done()
            invalid=[([['TEXT','重复','','','T0'],['TEXT','重复','','','T0']],'duplicate TEXT markers'),([['','普通标志','','TM.A1.value==1','T0'],['TEXT','重复','','','T0']],'mixed duplicate markers'),([['TEXT','错误标志','','','F']],'unknown TEXT marker'),([['','前向','','TM.A1.value==1','','T0:1'],['TEXT','稍后','','','T0']],'forward reference'),([['PAR','开始'],['TEXT','并行标志','','','T0'],['','条件','','TM.A1.value==1'],['CHANGE','结束']],'TEXT marker inside parallel')]
            for rows,label in invalid:check(bool(preview(rows)['definitions'][0]['errors']),label+' is rejected')
            def add(name,rows):
                p=preview([],timeout=2,sheets=[(name,rows)]);item=p['definitions'][0];assert not item['errors'],item['errors'];c.call('/api/sequence/apply',dict(token=p['token'],ids=[item['definition']['Id']],mode='add'));return next(d for d in c.call('/api/sequence/config')['definitions'] if d['Id']==item['definition']['Id'])
            target=add('TEXT目标',[['TEXT','新起点','','','T0'],['','新时刻','','TM.A1.value==1','','T0:.3']]);source=add('TEXT来源',[['TEXT','旧起点','','','T0'],['CALL','跳转',target['Code'],'','','F:.3']]);current=source['Id'];start();wait(lambda:state()['runtime']['id']==target['Id'],'jump to marker target');current=target['Id'];s=state();check(s['runtime']['t0'] is not None and s['runtime']['t0']<.15 and s['rows'][1]['planned']<.45,'CALL target establishes a fresh TEXT T0');row_status(3,'等待参数');data(1);done()
            check(bool(preview([['CALL','跳转',target['Code']],['TEXT','不可执行标志','','','T0']])['definitions'][0]['errors']),'marker after CALL is rejected rather than silently established')
            check(not preview([['CALL','跳转',target['Code']],['TEXT','尾注']])['definitions'][0]['errors'],'unmarked trailing TEXT after CALL remains allowed')
            check(bool(preview([['CALL','跳转',target['Code']],['TEXT','定时尾注','','','','F:2']])['definitions'][0]['errors']),'timed trailing TEXT after terminal CALL is rejected rather than silently passed')
        finally:
            if proc and proc.poll() is None:proc.terminate();proc.wait(timeout=6)
            receiver.close();sender.close()
    print('All',len(checks),'TEXT T0 checks passed. Traffic used loopback only.')
if __name__=='__main__':main()
