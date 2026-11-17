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
        def edit(id=None):return c.call('/api/sequence/edit-preview',dict(id=id or current,revision=c.call('/api/sequence/config')['revision']))
        def checked(p,name=None,rows=None):
            d=p['definitions'][0]['definition'];return c.call('/api/sequence/recheck',dict(token=p['token'],id=d['Id'],name=name or d['Name'],rows=rows or d['Rows']))
        def save(p,expected=200):return c.call('/api/sequence/save-edits',dict(token=p['token']),expected=expected)
        def definition(id=None):return c.call('/api/sequence/definition?id='+(id or current))['definition']
        def add(name,rows):
            p=preview([],sheets=[(name,rows)]);d=p['definitions'][0]['definition'];assert not p['definitions'][0]['errors'];c.call('/api/sequence/apply',dict(token=p['token'],ids=[d['Id']],mode='add'));return definition(d['Id'])
        try:
            start_server();password=secrets.token_urlsafe(18);c.call('/api/auth/setup',dict(username='editqa',name='Edit QA',password=password));c.csrf=c.call('/api/auth/login',dict(username='editqa',password=password))['csrf']
            install([['TEXT','基准','','','T0'],['','等待','','TM.A1.value==1','','T0:.1','2']],timeout=2);original=definition();disk=(root/'config/sequences.json').read_bytes();p=edit()
            check(p['editId']==current and p['definitions'][0]['definition']==original and (root/'config/sequences.json').read_bytes()==disk,'opening an edit draft does not modify the saved sequence')
            c.call('/api/sequence/edit-preview',dict(id=current,revision=c.call('/api/sequence/config')['revision']),expected=403,csrf=False);c.call('/api/sequence/save-edits',dict(token=p['token']),expected=403,csrf=False);check(True,'edit and save enforce CSRF')
            rows=p['definitions'][0]['definition']['Rows'];rows[1]['Condition']='TM.NOT_FOUND.value==3';bad=checked(p,'新名字',rows);check(bool(bad['definitions'][0]['errors']),'invalid edited references remain visible with errors');save(bad,400);check((root/'config/sequences.json').read_bytes()==disk,'invalid edits cannot overwrite the saved file')
            rows=bad['definitions'][0]['definition']['Rows'];rows[0].update(Keyword='TEXT',Description='新起点',Mark='T0');rows[1].update(Keyword='',Description='新步骤',Command='K001',Condition='TM.A1.value==3',Expected='T0:.2',Timeout='1.4');p=checked(bad,' 编辑后名称 ',rows);check(not p['definitions'][0]['errors'] and p['editId']==current,'rechecking retains edit identity and accepts seven-column changes')
            c.call('/api/sequence/apply',dict(token=p['token'],ids=[current],mode='replace'),expected=400);check(True,'editing token cannot replace the whole library through the import API')
            save(p);saved=definition();check(saved['Name']=='编辑后名称' and saved['Rows'][1]['Condition']=='TM.A1.value==3' and saved['Rows'][1]['Command']=='K001' and saved['Rows'][1]['Expected']=='T0:.2' and saved['Rows'][1]['Timeout']=='1.4','edited name, command, predicate, marker and scheduling columns persist')
            check(all(saved[k]==original[k] for k in ['Id','Code','Mode','SourceIP','Filename','Sheet']) and len(c.call('/api/sequence/config')['definitions'])==1,'save preserves code, identity, mode, source and provenance without adding a duplicate')
            source=add('跳转来源',[['CALL','跳转',saved['Code']]]);other=add('其他序列',[['TEXT','说明']]);p=edit();p=checked(p,other['Name']);before=(root/'config/sequences.json').read_bytes();save(p,400);check((root/'config/sequences.json').read_bytes()==before,'duplicate names are rejected without touching disk')
            p=edit();p=checked(p,'保留CALL目标');save(p);check(definition(source['Id'])['Rows'][0]['Command']==saved['Code'] and not c.call('/api/sequence/definition?id='+source['Id'])['errors'],'renaming a target preserves incoming CALL references')
            p=edit();d=p['definitions'][0]['definition'];d['Rows'][1]['Keyword']='CALL';d['Rows'][1]['Command']=source['Code'];d['Rows'][1]['Condition']='';p=checked(p,rows=d['Rows']);check(bool(p['definitions'][0]['errors']),'a cyclic CALL edit is detected');save(p,400)
            p=edit();p=checked(p,'不会被保存的草稿');check(definition()['Name']=='保留CALL目标','abandoning a checked draft leaves the original name untouched')
            cfg=c.call('/api/sequence/config');c.call('/api/sequence/config',dict(id='',mode='judge',sourceIP='',defaultTimeout=3,revision=cfg['revision'],delete='false'));save(p,409);check(definition()['Name']=='保留CALL目标','stale drafts cannot overwrite a newer configuration revision')
            imported=preview([['TEXT','普通导入']]);save(imported,400);check(True,'ordinary import tokens cannot impersonate stored-sequence editing')
            c.call('/api/control',dict(action='start'));start();p=edit();p=checked(p,'运行时禁止保存');save(p,409);check(definition()['Name']=='保留CALL目标','save is blocked while a sequence is running');control('stop')
            process_id=proc.pid;proc.terminate();proc.wait(timeout=6);start_server();c.csrf=c.call('/api/auth/login',dict(username='editqa',password=password))['csrf'];check(definition()['Name']=='保留CALL目标' and definition()['Code']==saved['Code'] and len(c.call('/api/sequence/config')['definitions'])==3,'edits survive restart without losing other sequences or changing the code')
            check(not state()['runtime']['running'],'saving and restarting never starts the sequence or sends commands');empty()
        finally:
            if proc and proc.poll() is None:proc.terminate();proc.wait(timeout=6)
            receiver.close();sender.close()
    print('All',len(checks),'stored-sequence editing checks passed. No active commands sent.')
if __name__=='__main__':main()
