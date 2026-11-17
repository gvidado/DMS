"""Full-byte file import and data-only live matching. All network traffic uses loopback."""
import csv,io,json,secrets,shutil,socket,struct,subprocess,sys,tempfile,urllib.parse,urllib.request
from pathlib import Path
from integration_test import ROOT,Client,free_port,wait
from runtime_benchmark import seed
from stc_xml_import_test import envelope,xml

def main():
    exe=Path(sys.argv[1]).resolve();checks=[]
    def check(ok,label):
        assert ok,label
        checks.append(label);print('PASS',label,flush=True)
    with tempfile.TemporaryDirectory(dir=ROOT/'test-output',prefix='command-policy-') as temporary:
        root=Path(temporary);seed(root,per_packet=2,packets=1)
        for folder in ['web','native','database']:shutil.copytree(ROOT/folder,root/folder)
        def save(name,value):(root/'config'/name).write_text(json.dumps(value),encoding='utf-8')
        settings=json.loads((root/'config/server.json').read_text('utf-8-sig'));settings['UdpPort']=free_port(socket.SOCK_DGRAM);save('server.json',settings)
        save('observed-commands.json',dict(SchemaVersion=1,SourceIP='127.0.0.1',NextNumber=1,Commands=[]))
        save('tc-bindings.json',dict(Revision='empty',Bindings=[]));save('sequences.json',dict(Revision='empty',Sequences=[]));save('history-storage.json',dict(DatabaseEnabled=False))
        target=socket.socket(socket.AF_INET,socket.SOCK_DGRAM);target.bind(('127.0.0.2',0));target.settimeout(3);source_port=free_port(socket.SOCK_DGRAM)
        other=socket.socket(socket.AF_INET,socket.SOCK_DGRAM);other.bind(('127.0.0.3',0));other.settimeout(3)
        save('command-send.json',dict(SourceIP='127.0.0.1',Routes=[dict(DestinationIP='127.0.0.2',SourcePort=source_port,DestinationPort=target.getsockname()[1])]))
        port=free_port();c=Client(port);password=secrets.token_urlsafe(18);process=None;log=(root/'server.log').open('wb')
        def launch():return subprocess.Popen([str(exe),'--local','--root',str(root),'--web-port',str(port)],stdout=log,stderr=subprocess.STDOUT,creationflags=subprocess.CREATE_NO_WINDOW)
        def ready():
            try:c.call('/api/auth/me');return True
            except OSError:return False
        def upload(raw,fmt='stc-xml',**query):
            request=urllib.request.Request(c.base+'/api/send-monitor/commands/preview?'+urllib.parse.urlencode(dict(format=fmt,**query)),data=raw,headers={'Content-Type':'application/octet-stream','X-CSRF-Token':c.csrf})
            return json.loads(c.opener.open(request,timeout=20).read())
        def apply(plan):return c.call('/api/send-monitor/commands/apply',dict(token=plan['token']))
        def disk():return json.loads((root/'config/observed-commands.json').read_text('utf-8-sig'))
        def entry(code):return next(e for e in disk()['Commands'] if any(k['Code']==code for k in e['Categories']))
        def csv_data(rows):
            out=io.StringIO();csv.writer(out).writerows([['指令编号','指令名称','指令类别','来源IP','目的IP','完整UDP载荷HEX','指令格式']]+rows);return out.getvalue().encode('utf-8')
        try:
            process=launch();wait(ready,'command policy server');c.call('/api/auth/setup',dict(username='policyqa',name='匹配规则验证',password=password));c.csrf=c.call('/api/auth/login',dict(username='policyqa',password=password))['csrf']
            first=upload(xml(('部件221',221,92,'AA39')),destinationIP='127.0.0.2');apply(first);base=bytes.fromhex(entry('ZK0001')['PayloadHex'])
            second=upload(xml(('部件222',222,92,'AA39')),destinationIP='127.0.0.2');check(second['canApply'] and second['addedCommands']==1 and second['skipped']==0,'same data with a different part ID is a distinct XML command');apply(second)
            third=upload(xml(('接口93',221,93,'AA39')),destinationIP='127.0.0.2');check(third['canApply'] and third['addedCommands']==1,'same data with a different interface type is a distinct XML command');apply(third)
            fourth=upload(xml(('不同帧计数',221,92,'AA39')),destinationIP='127.0.0.2',frameCounter='0x1234');check(fourth['canApply'] and fourth['addedCommands']==1,'file frame counter differences are included in full UDP identity');apply(fourth)
            same=upload(xml(('重复文件',221,92,'AA39')),destinationIP='127.0.0.2');check(same['skippedExisting']==1 and not same['canApply'],'only exact complete UDP equality skips XML reimport')
            contained=upload(xml(('包含数据但不同包',221,92,'FFAA3900')),destinationIP='127.0.0.2');check(contained['canApply'] and contained['skipped']==0,'containing another command data area does not skip a file import');apply(contained)
            manual=c.call('/api/send-monitor/commands/preview?format=stc-manual',dict(name='手动部件223',bujianid='223',gongnengid='92',datastr='AA39',destinationIP='127.0.0.2'));check(manual['canApply'] and manual['xmlRows'][0]['code']=='ZK0006','manual entry follows full-payload identity like structured file import');apply(manual)
            different=envelope(bytes.fromhex('AA39'),counter=0x5678)
            imported=upload(csv_data([['ZK0010','CSV完整包','ZK','127.0.0.1','127.0.0.2',different.hex(' '),'STC']]),fmt='');check(imported['canApply'] and imported['addedCommands']==1,'CSV preserves same data under another complete UDP packet');apply(imported)
            conflict=upload(csv_data([['ZK0001','不能覆盖','ZK','127.0.0.1','127.0.0.2',different.hex(' '),'STC']]),fmt='');check(not conflict['canApply'] and any('占用' in e for e in conflict['errors']),'an existing code cannot be reused for another full UDP payload')
            exported=c.opener.open(c.base+'/api/send-monitor/commands/export.csv').read();reimport=upload(exported,fmt='');check(reimport['canApply'] and reimport['addedCommands']==0,'CSV export/import retains every full-packet variant without merging');apply(reimport)
            before=len(disk()['Commands']);originals={e['Number']:e['PayloadHex'] for e in disk()['Commands']}
            rules=c.call('/api/continuous-rules')
            for row,code in zip(rules['rows'],['ZK0001','ZK0002']):
                rule=row['rule'];rule.update(Enabled=True,Script='InitRegion this.state=0; EndInitRegion StateRegion if(TC.'+code+') then this.state=this.state+1; end EndStateRegion ResultRegion RET=Normal; EndResultRegion');current=c.call('/api/continuous-rules');c.call('/api/continuous-rules/save',dict(rule=rule,revision=current['revision']))
            adapter=next(i for i in c.call('/api/send-monitor/interfaces')['interfaces'] if i['loopback']);c.call('/api/send-monitor/config',dict(SourceIPs=['127.0.0.1'],DestinationIPs=['127.0.0.2','127.0.0.3'],InterfaceId=adapter['id'],DestinationPort=0))
            c.call('/api/control',dict(action='start'));c.call('/api/continuous-rules/control',dict(action='start'))
            sender=socket.socket(socket.AF_INET,socket.SOCK_DGRAM);sender.bind(('127.0.0.1',0));sent=0
            def send(data,receiver=target):
                nonlocal sent
                sender.sendto(data,receiver.getsockname());assert receiver.recvfrom(65535)[0]==data;sent+=1;wait(lambda:c.call('/api/send-monitor/state')['total']==sent,'captured datagram')
            def states():return [row['currentState'] for row in c.call('/api/continuous-rules')['rows']]
            variant=bytearray(base);variant[4:20]=bytes(range(16));variant[-4:]=b'\x00'*4;variant=bytes(variant)
            send(variant);wait(lambda:states()==[1,0],'structured matched TC events')
            record=c.call('/api/send-monitor/list')['records'][0];expected=['ZK0001','ZK0004','ZK0010']
            check(all(code in record['commandCode'] for code in expected) and 'ZK0005' not in record['commandCode'],'one UDP record displays all exact data-area matches, without containment matches')
            check(states()==[1,0] and c.call('/api/send-monitor/state')['total']==1,'one physical UDP triggers only function/interface-compatible TC events and counts once')
            check(len(disk()['Commands'])==before and all(e['PayloadHex']==originals[e['Number']] for e in disk()['Commands']),'UDP ignores length, counters and checksum but retains function/interface identity without replacing file commands')
            send(variant);wait(lambda:states()==[2,0],'repeated packet triggers once per TC');check(len(disk()['Commands'])==before,'repeated matching UDP creates no new library entry')
            csv_rows=list(csv.reader(io.StringIO(c.opener.open(c.base+'/api/send-monitor/export').read().decode('utf-8-sig'))));check(all(code in csv_rows[1][-2] for code in expected),'send-monitor CSV also contains every matched command code')
            send(variant,other);wait(lambda:states()==[3,0],'all matches at new destination');check(all('127.0.0.3' in entry(code)['DestinationIPs'] for code in expected) and '127.0.0.3' not in entry('ZK0002')['DestinationIPs'] and len(disk()['Commands'])==before,'a new destination is recorded only for the matching function/interface group')
            changed=bytearray(variant);changed[26]=0x55;send(bytes(changed));check(len(disk()['Commands'])==before+1,'changing actual data creates a new observed command')
            send(b'\x01\x02\x03');send(b'\x01\x02\x03');check(len(disk()['Commands'])==before+2,'non-EB90 UDP falls back to exact raw-payload deduplication')
            send(b'\x01\x02\x04');check(len(disk()['Commands'])==before+3,'different raw UDP data remains distinct')
            sender.close();c.call('/api/control',dict(action='stop'))
            incoming_only=envelope(bytes.fromhex('5539'));p=upload(xml(('接收同数据新完整包',221,92,'5539')),destinationIP='127.0.0.2');check(p['canApply'] and p['addedCommands']==1,'file import creates a full-packet variant even when UDP already stored the same content');apply(p)
            binary=upload(bytes.fromhex('11223344'),fmt='injection-bin',filename='YKE0001-policy.bin',destinationIP='127.0.0.2',kind='YKE');check(binary['canApply'] and binary['addedCommands']==1,'BIN import uses the common full-payload import path');apply(binary)
            repeated=upload(bytes.fromhex('11223344'),fmt='injection-bin',filename='YKE0001-policy.bin',destinationIP='127.0.0.2',kind='YKE');check(repeated['canApply'] and repeated['addedCommands']==0,'identical BIN-generated full UDP reimports without duplication')
            for code,expected_packet in [('ZK0001',base),('ZK0002',envelope(bytes.fromhex('AA39'),function=222))]:
                definition=entry(code);p=c.call('/api/command-send/preview',dict(number=definition['Number'],code=code,destinations=['127.0.0.2']));check(bytes.fromhex(p['hex'])==expected_packet,'active send preview retains full fields for '+code);c.call('/api/command-send/send',dict(token=p['token']));received,origin=target.recvfrom(65535);check(received==expected_packet and origin[1]==source_port,'active send transmits distinct stored full packet for '+code)
            snapshot=(root/'config/observed-commands.json').read_bytes();count=len(disk()['Commands']);process.terminate();process.wait(10);process=launch();c=Client(port);wait(ready,'policy restart');c.csrf=c.call('/api/auth/login',dict(username='policyqa',password=password))['csrf']
            check((root/'config/observed-commands.json').read_bytes()==snapshot and len(disk()['Commands'])==count,'restart retains all header variants, codes and raw observed payloads without merging')
            (exe.parent/'command-match-policy-report.json').write_text(json.dumps(dict(checks=len(checks),details=checks),ensure_ascii=False,indent=2),encoding='utf-8')
        except Exception:
            log.flush();print((root/'server.log').read_text('utf-8',errors='replace')[-2000:]);raise
        finally:
            if process and process.poll() is None:process.terminate();process.wait(10)
            target.close();other.close();log.close()
    print('All',len(checks),'command identity policy checks passed. Loopback only.')

if __name__=='__main__':main()
