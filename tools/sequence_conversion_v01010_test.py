"""Confirmed converter priority and PLUSE send-after-resume, on isolated loopback."""
import io,json,secrets,shutil,socket,subprocess,sys,tempfile,time,urllib.parse,urllib.request,zipfile
from pathlib import Path
from integration_test import ROOT,Client,free_port,wait
from runtime_benchmark import seed
from sequence_test import xlsx

def main():
    exe=Path(sys.argv[1]).resolve();host=Path(sys.argv[2]).resolve() if len(sys.argv)>2 else None;checks=[]
    def check(ok,label):
        assert ok,label
        checks.append(label);print('PASS',label,flush=True)
    with tempfile.TemporaryDirectory(dir=ROOT/'test-output',prefix='sequence-v01010-') as temporary:
        root=Path(temporary);seed(root,per_packet=2,packets=1)
        for name in ['web','native','database']:shutil.copytree(ROOT/name,root/name)
        def save(name,value):(root/'config'/name).write_text(json.dumps(value),encoding='utf-8')
        udp,web=free_port(socket.SOCK_DGRAM),free_port()
        settings=json.loads((root/'config/server.json').read_text(encoding='utf-8-sig'));settings['UdpPort']=udp;save('server.json',settings)
        commands=[dict(Number=i,Name=name,DestinationIP=ip,DestinationIPs=[ip],PayloadHex=f'AA {i:02X}',
                       Categories=[dict(Kind=kind,Code=code,DestinationIPs=[ip])]) for i,name,ip,kind,code in [
            (1,'点火','192.168.1.113','BC','BC2133'),(2,'关机','192.168.1.111','PSS','PSS001'),
            (3,'编号绑定测试','192.168.1.113','BC','BC4001'),(4,'广播','192.168.1.113','BC','BC0001')]]
        commands.append(dict(Number=5,Name='暂停后发令',DestinationIP='127.0.0.2',DestinationIPs=['127.0.0.2'],PayloadHex='DE AD 10 10'))
        save('observed-commands.json',dict(SchemaVersion=1,SourceIP='127.0.0.1',IdentityRule='udp-payload',NextNumber=6,Commands=commands))
        save('tc-bindings.json',dict(SchemaVersion=1,Revision='pluse',Bindings=[dict(Code='K001',Number=5),dict(Code='YKE0021',Number=3)]))
        save('continuous-rules.json',dict(SchemaVersion=1,Revision='empty',Rules=[]))
        receiver=socket.socket(socket.AF_INET,socket.SOCK_DGRAM);receiver.bind(('127.0.0.2',0));receiver.settimeout(.2)
        source_port=free_port(socket.SOCK_DGRAM)
        save('command-send.json',dict(SourceIP='127.0.0.1',Routes=[dict(DestinationIP='127.0.0.2',SourcePort=source_port,DestinationPort=receiver.getsockname()[1])]))
        c=Client(web);args=[str(host),str(root),str(web)] if host else [str(exe),'--local','--root',str(root),'--web-port',str(web)]
        process=subprocess.Popen(args,stdin=subprocess.PIPE if host else None,text=True,encoding='utf-8',
            stdout=(root/'server.log').open('wb'),stderr=subprocess.STDOUT,creationflags=subprocess.CREATE_NO_WINDOW)
        def ready():
            try:c.call('/api/auth/me');return True
            except OSError:return False
        def upload(rows,convert=True,mode='execute',timeout=2):
            url=c.base+'/api/sequence/preview?'+urllib.parse.urlencode(dict(filename='流程.xlsx',mode=mode,timeout=timeout,sourceIP='127.0.0.1',convert='commands' if convert else ''))
            request=urllib.request.Request(url,data=xlsx([('流程'+secrets.token_hex(3),rows)]),headers={'Content-Type':'application/octet-stream','X-CSRF-Token':c.csrf})
            try:return json.loads(c.opener.open(request,timeout=15).read())
            except urllib.error.HTTPError as error:raise AssertionError(error.read().decode('utf-8')) from error
        def install(rows,timeout=2):
            if c.call('/api/state')['mode']!='stopped':c.call('/api/control',dict(action='stop'))
            preview=upload(rows,False,timeout=timeout);item=preview['definitions'][0]
            assert not item['errors'],item['errors']
            identifier=item['definition']['Id'];c.call('/api/sequence/apply',dict(token=preview['token'],ids=[identifier],mode='add'))
            return identifier
        def state(run):return c.call('/api/sequence/state?runId='+run)
        def start(identifier):
            if c.call('/api/state')['mode']=='stopped':c.call('/api/control',dict(action='start'))
            cfg=c.call('/api/sequence/config')
            return c.call('/api/sequence/control',dict(action='start',id=identifier,runName='PLUSE确认',revision=cfg['revision'],requestId=secrets.token_hex(16)))['runId']
        def no_send():
            try:receiver.recvfrom(65535);return False
            except socket.timeout:return True
        try:
            wait(ready,'converter server');password=secrets.token_urlsafe(20)
            c.call('/api/auth/setup',dict(username='pluseqa',name='流程验证',password=password));c.csrf=c.call('/api/auth/login',dict(username='pluseqa',password=password))['csrf']
            rows=[
                ['','说明无编号','KUTC.YBC2133'],
                ['TEXT','说明提到PSS001但指令优先','bc2133'],
                ['TEXT','核对PSS001状态','UNKNOWN'],
                ['','检查yke0021后数据'],
                ['','不含库内编号','UNKNOWN'],
                ['PLUSE','暂停','UTC.YBC0001'],
                ['PLUSE','说明BC2133'],
                ['PLUSE','说明BC2133','UNKNOWN'],
                ['PAR','并行开始'],['HOLD','保持','','TME.A0.value>0'],['CHANGE','并行结束'],
                ['','纯参数判读','','TME.A0.value>0'],
                ['TEXT','参数列内容','TME.A0.value'],
                ['CALL','调用','XLP9999'],
                ['','多个BC2133与PSS001'],
                ['','只按编号不按名称','点火'],
                ['T0','建立起点'],['','边界','BC213300'],
                ['','未入库注入','YKM9999']]
            converted=upload(rows);out=converted['definitions'][0]['definition']['Rows']
            check(out[0]['Command']=='BC2133' and out[0]['Keyword']=='','existing library command wins without requiring a description match')
            check(out[1]['Command']=='BC2133' and out[1]['Keyword']=='','case and legacy prefixes normalize, TEXT clears, and description cannot replace a valid command')
            check(out[2]['Command']=='PSS001' and out[2]['Keyword']=='','description rescues a nonempty unmatched command and clears TEXT')
            check(out[3]['Command']=='YKE0021' and out[3]['Keyword']=='','bare lowercase YKE code in description fills an empty command cell')
            check(out[4]['Keyword']=='TEXT' and out[4]['Command']=='UNKNOWN','unknown ordinary rows become TEXT without losing original command text')
            check(out[5]['Keyword']=='PLUSE' and out[5]['Command']=='BC0001','PLUSE keyword remains while its command normalizes')
            check(out[6]['Keyword']=='PLUSE' and out[6]['Command']=='' and out[7]['Keyword']=='PLUSE' and out[7]['Command']=='UNKNOWN','PLUSE is preserved and its command is not inferred from description')
            check([out[i]['Keyword'] for i in [8,9,10,13]]==['PAR','HOLD','CHANGE','CALL'],'parallel controls and CALL are never converted into TEXT')
            check(out[11]['Keyword']=='' and out[12]['Keyword']=='TEXT' and out[12]['Command']=='TME.A0.value','pure parameter rows retain their original judgment role and keyword')
            check(out[14]['Keyword']=='TEXT' and out[14]['Command']=='' and any(r['row']==16 and r['warning'] for r in converted['conversion']),'multiple candidate commands are reported without an arbitrary choice')
            check(out[15]['Keyword']=='TEXT' and out[17]['Keyword']=='TEXT','names and longer unmatched identifiers do not qualify as library codes')
            check(out[16]['Mark']=='T0' and out[16]['Keyword']=='TEXT','misplaced T0 still moves into the mark column')
            check(any('第20行YKE/YKM指令未入库' in r['reason'] for r in converted['conversion']),'missing YKE/YKM code retains its original Excel row diagnostic')
            request=urllib.request.Request(c.base+'/api/sequence/export-converted',data=json.dumps(dict(token=converted['token'])).encode(),headers={'Content-Type':'application/json','X-CSRF-Token':c.csrf})
            data=c.opener.open(request).read()
            with zipfile.ZipFile(io.BytesIO(data)) as workbook:check(workbook.testzip() is None and b'<f>' not in workbook.read('xl/worksheets/sheet1.xml'),'converted output remains a literal-string Excel workbook')
            if '--eleven' in sys.argv:
                from sequence_eleven_columns_test import run_checks
                run_checks(c,root,exe.parent,check,password,browser='--browser' in sys.argv)
            if '--shorthand' in sys.argv:
                from sequence_shorthand_test import run_checks
                run_checks(c,root,exe.parent,check)
            identifier=install([['PLUSE','先暂停再发令','K001'],['TEXT','结束']])
            c.call('/api/control',dict(action='start'))
            for attempt in range(2):
                run=start(identifier);wait(lambda:state(run)['runtime']['paused'],'PLUSE pause')
                check(no_send(),'PLUSE sends no UDP before the user resumes, run '+str(attempt+1))
                paused=state(run)['runtime']['elapsed'];time.sleep(.2)
                check(abs(state(run)['runtime']['elapsed']-paused)<.03,'PLUSE freezes sequence timing while paused')
                if host and attempt==0:
                    draft=c.call('/api/decoders?draft=1');field=dict(draft['configuration']['Catalog']['Parameters'][0])
                    field['Scale']=2
                    c.call('/api/decoders/parameter',dict(id=field['Id'],revision=draft['revision'],parameter=field))
                    assert c.call('/api/configuration/schedule')['pending']
                    process.stdin.write('tick\n');process.stdin.flush()
                    wait(lambda:not c.call('/api/configuration/schedule')['pending'],'configuration applies')
                    check(state(run)['runtime']['paused'] and no_send()
                          and any(row['status']=='人工暂停' for row in state(run)['rows']),
                          'automatic configuration activation preserves PLUSE pause and does not send before resume')
                revision=state(run)['runtime']['controlRevision']
                c.call('/api/sequence/control',dict(action='resume',runId=run,controlRevision=str(revision)))
                receiver.settimeout(3);data,source=receiver.recvfrom(65535);receiver.settimeout(.2)
                check(data==bytes.fromhex('DE AD 10 10') and source[1]==source_port,'resuming PLUSE sends the selected command from its configured source port')
                wait(lambda:not state(run)['runtime']['running'],'sequence completes')
                history=c.call('/api/sequence/history?runId='+run)['rows']
                check(sum(r['Event']=='指令发送' for r in history)==1 and no_send(),'PLUSE completes and advances after exactly one send')
                c.call('/api/sequence/control',dict(action='resume',runId=run,controlRevision=str(revision)),expected=409)
            empty=install([['PLUSE','只有暂停'],['TEXT','结束']]);run=start(empty);wait(lambda:state(run)['runtime']['paused'],'empty PLUSE')
            c.call('/api/sequence/control',dict(action='resume',runId=run,controlRevision=str(state(run)['runtime']['controlRevision'])))
            wait(lambda:not state(run)['runtime']['running'],'empty PLUSE completes')
            check(no_send(),'PLUSE without a command resumes without sending UDP')
            bad=install([['','等待参数','','TME.A0.value>50','','','.2']],timeout=.2);run=start(bad)
            wait(lambda:not state(run)['runtime']['running'],'native sequence timeout')
            pending=c.call('/api/abnormal-records?status=pending')['records']
            check(any(r['key'].startswith('sequence:') for r in pending),'actual sequence failure creates a native sequence incident')
            result=c.call('/api/abnormal-records/release-all',dict(reason='序列异常批量确认'))
            released=c.call('/api/abnormal-records?status=released')['records']
            check(result['ok'] and any(r['runId']==run and r['reason']=='序列异常批量确认' for r in released),'release-all includes native sequence incidents and saves the reason')
            c.call('/api/control',dict(action='stop'))
            (exe.parent/'sequence-v01010-report.json').write_text(json.dumps(dict(checks=len(checks),details=checks),ensure_ascii=False,indent=2),encoding='utf-8')
        except Exception:
            print((root/'server.log').read_text(encoding='utf-8',errors='replace')[-2000:]);raise
        finally:
            receiver.close();process.terminate();process.wait(10)

if __name__=='__main__':main()
