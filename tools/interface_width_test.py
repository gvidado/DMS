"""Migrate actual pre-width-rule STC records and preserve references. Loopback only."""
import csv,io,json,secrets,shutil,socket,struct,subprocess,sys,tempfile,urllib.parse,urllib.request
from pathlib import Path
from integration_test import ROOT,Client,free_port,wait
from runtime_benchmark import seed
from sequence_test import xlsx
from stc_xml_import_test import envelope

def legacy(data,function=221,interface=92):
    b=struct.pack('<4sIHIIHHI',bytes.fromhex('EB90EB90'),len(data)+22,170,1,1,0,function,interface)+data
    return b+struct.pack('<I',sum(b))

def main():
    exe=Path(sys.argv[1]).resolve();old_exe=Path(sys.argv[2]).resolve();checks=[]
    def check(ok,label):
        assert ok,label
        checks.append(label);print('PASS',label,flush=True)
    with tempfile.TemporaryDirectory(dir=ROOT/'test-output',prefix='interface-width-') as temporary:
        root=Path(temporary);seed(root,per_packet=2,packets=1)
        for folder in ['web','native','database']:shutil.copytree(ROOT/folder,root/folder)
        def save(name,value):(root/'config'/name).write_text(json.dumps(value),encoding='utf-8')
        settings=json.loads((root/'config/server.json').read_text('utf-8-sig'));settings['UdpPort']=free_port(socket.SOCK_DGRAM);save('server.json',settings)
        old_payloads=[legacy(bytes.fromhex('AA39'),222),legacy(bytes.fromhex('AA39'),0x1553,0x12345678),legacy(bytes.fromhex('CC41'),65535,0x12345678),legacy(bytes.fromhex('DD42'))]
        entries=[dict(Number=i,Name='原始名称'+str(i),STC=i<4,DestinationIP='127.0.0.2',DestinationIPs=['127.0.0.2'],FirstSeenUtc='2026-11-01T12:00:00Z',PayloadHex=b.hex(' '),Categories=[dict(Kind='ZK',Code='ZK'+str(i).zfill(4),DestinationIPs=['127.0.0.2'])]) for i,b in enumerate(old_payloads,1)]
        save('observed-commands.json',dict(SchemaVersion=1,SourceIP='127.0.0.1',NextNumber=5,Commands=entries))
        save('tc-bindings.json',dict(Revision='old',Bindings=[]));save('sequences.json',dict(Revision='old',Sequences=[]));save('history-storage.json',dict(DatabaseEnabled=False))
        receiver=socket.socket(socket.AF_INET,socket.SOCK_DGRAM);receiver.bind(('127.0.0.2',0));receiver.settimeout(3)
        source_port=free_port(socket.SOCK_DGRAM);save('command-send.json',dict(SourceIP='127.0.0.1',Routes=[dict(DestinationIP='127.0.0.2',SourcePort=source_port,DestinationPort=receiver.getsockname()[1])]))
        port=free_port();c=Client(port);password=secrets.token_urlsafe(18);process=None;log=(root/'server.log').open('wb')
        def launch(binary):return subprocess.Popen([str(binary),'--local','--root',str(root),'--web-port',str(port)],stdout=log,stderr=subprocess.STDOUT,creationflags=subprocess.CREATE_NO_WINDOW)
        def ready():
            try:c.call('/api/auth/me');return True
            except OSError:return False
        def login():c.csrf=c.call('/api/auth/login',dict(username='widthqa',password=password))['csrf']
        def disk():return json.loads((root/'config/observed-commands.json').read_text('utf-8-sig'))
        def stop():process.terminate();process.wait(10)
        def upload(data,path):
            req=urllib.request.Request(c.base+path,data=data,headers={'Content-Type':'application/octet-stream','X-CSRF-Token':c.csrf})
            return json.loads(c.opener.open(req,timeout=20).read())
        try:
            process=launch(old_exe);wait(ready,'old server');c.call('/api/auth/setup',dict(username='widthqa',name='迁移验证',password=password));login()
            bindings=c.call('/api/continuous-rules/tc');c.call('/api/continuous-rules/tc',dict(number=1,code='K001',revision=bindings['revision']))
            button=c.call('/api/command-panels/bind',dict(number=1,code='ZK0001'))['button'];button['Id']='button1'
            panel=c.call('/api/command-panels')['configuration'];panel['Screens'][0]['Buttons']=[button];c.call('/api/command-panels',dict(configuration=panel))
            plan=upload(xlsx([('旧序列',[['','保留引用','ZK0001','','','0.2','2']])]),'/api/sequence/preview?filename=old.xlsx&mode=execute&timeout=2&sourceIP=127.0.0.1')
            sid=plan['definitions'][0]['definition']['Id'];c.call('/api/sequence/apply',dict(token=plan['token'],ids=[sid],mode='replace'))
            references={name:(root/'config'/name).read_bytes() for name in ['tc-bindings.json','sequences.json']}
            check(len(disk()['Commands'])==4 and 'InterfaceWidthRule' not in disk(),'actual previous version accepts the old four-byte STC library')
            stop();c=Client(port);process=launch(exe);wait(ready,'new server');login();current=disk();rows={r['Number']:r for r in current['Commands']}
            check(current['InterfaceWidthRule']==2 and len(rows)==4,'migration marks the new width rule without losing command entries')
            check(bytes.fromhex(rows[1]['PayloadHex'])==envelope(bytes.fromhex('AA39'),function=222) and rows[1]['InterfaceBytes']==2,'legacy non-0x1553 command removes exactly two interface bytes and recomputes length/sum')
            check(bytes.fromhex(rows[2]['PayloadHex'])==old_payloads[1] and rows[2]['InterfaceBytes']==4,'0x1553 legacy commands retain every original byte')
            check(all(rows[i]['Name']==entries[i-1]['Name'] and rows[i]['Categories']==entries[i-1]['Categories'] and rows[i]['DestinationIPs']==entries[i-1]['DestinationIPs'] and rows[i]['FirstSeenUtc']==entries[i-1]['FirstSeenUtc'] for i in rows),'migration preserves record numbers, names, command codes, destinations and original creation time')
            check(bytes.fromhex(rows[3]['PayloadHex'])==old_payloads[2] and rows[3]['InterfaceBytes']==4 and '65535' in rows[3]['FormatIssue'],'out-of-range legacy interface is preserved and marked rather than truncated')
            check(bytes.fromhex(rows[4]['PayloadHex'])==old_payloads[3] and not rows[4]['STC'],'untyped captured packets are preserved instead of guessing their old interface width')
            check(all((root/'config'/name).read_bytes()==value for name,value in references.items()),'manual TC aliases and stored sequence references are unchanged')
            restored=c.call('/api/command-panels');check(restored['bindings'][0]['available'] and restored['configuration']['Screens'][0]['Buttons'][0]['CommandIdentity']==button['CommandIdentity'],'existing command-panel content binding survives the automatic conversion')
            listing=c.call('/api/send-monitor/commands/list');check(next(r for r in listing['rows'] if r['number']==3)['formatIssue'],'command catalog exposes the exact legacy format problem')
            error=c.call('/api/command-send/preview',dict(number=3,code='ZK0003'),expected=400);check('65535' in error['error'],'invalid legacy entry is blocked before a send token can be created')
            for number,expected,start,width in [(1,envelope(bytes.fromhex('AA39'),function=222),25,2),(2,old_payloads[1],27,4)]:
                p=c.call('/api/command-send/preview',dict(number=number,code='ZK'+str(number).zfill(4)))
                check(bytes.fromhex(p['hex'])==expected and p['dataContentHex']=='AA 39' and p['dataContentStart']==start and p['interfaceTypeBytes']==width,'preview uses the correct data boundary for interface width '+str(width))
                c.call('/api/command-send/send',dict(token=p['token']));received,origin=receiver.recvfrom(65535)
                check(received==expected and origin==('127.0.0.1',source_port),'real loopback send retains exact migrated bytes for width '+str(width))
            exported=c.opener.open(c.base+'/api/send-monitor/commands/export.csv').read();csv_rows=list(csv.reader(io.StringIO(exported.decode('utf-8-sig'))))
            check(next(r for r in csv_rows if r[0]=='ZK0003')[-1]=='STC-LEGACY4','CSV export marks retained invalid legacy width explicitly')
            p=upload(exported,'/api/send-monitor/commands/preview');check(p['canApply'] and not p['addedCommands'],'CSV reimport retains every complete-packet identity including the legacy exception');c.call('/api/send-monitor/commands/apply',dict(token=p['token']))
            for row,code in zip(c.call('/api/continuous-rules')['rows'],['K001','ZK0002']):
                rule=row['rule'];rule.update(Enabled=True,Script='InitRegion this.state=0; EndInitRegion StateRegion if(TC.'+code+') then this.state=this.state+1; end EndStateRegion ResultRegion RET=Normal; EndResultRegion')
                c.call('/api/continuous-rules/save',dict(rule=rule,revision=c.call('/api/continuous-rules')['revision']))
            adapter=next(i for i in c.call('/api/send-monitor/interfaces')['interfaces'] if i['loopback']);c.call('/api/send-monitor/config',dict(SourceIPs=['127.0.0.1'],DestinationIPs=['127.0.0.2'],InterfaceId=adapter['id'],DestinationPort=0));c.call('/api/control',dict(action='start'));c.call('/api/continuous-rules/control',dict(action='start'))
            with socket.socket(socket.AF_INET,socket.SOCK_DGRAM) as sender:
                sender.bind(('127.0.0.1',0))
                for count,raw in enumerate([envelope(bytes.fromhex('AA39'),function=222),envelope(bytes.fromhex('AA39'),0x1553,0x12345678)],1):
                    sender.sendto(raw,receiver.getsockname());assert receiver.recvfrom(65535)[0]==raw
                    wait(lambda:[r['currentState'] for r in c.call('/api/continuous-rules')['rows']]==([1,0] if count==1 else [1,1]),'width-specific TC matching')
                    record=c.call('/api/send-monitor/list')['records'][0]
                    check(record['commandCode']==('ZK0001' if count==1 else 'ZK0002') and c.call('/api/send-monitor/state')['total']==count,'incoming width '+str(2 if count==1 else 4)+' matches only the command with the correct function and interface')
            c.call('/api/control',dict(action='stop'));before=(root/'config/observed-commands.json').read_bytes();stop();c=Client(port);process=launch(exe);wait(ready,'second new start');login()
            check((root/'config/observed-commands.json').read_bytes()==before,'a second startup never shrinks already-migrated command data again')
            c.call('/api/command-send/preview',dict(number=3,code='ZK0003'),expected=400);check(c.call('/api/command-panels')['bindings'][0]['available'],'retained overflow block and valid panel binding both survive CSV import and restart')
            fresh=dict(SchemaVersion=1,InterfaceWidthRule=2,SourceIP='127.0.0.1',Commands=[dict(entries[0],PayloadHex=envelope(bytes.fromhex('0000AA39'),function=222).hex(' '))]);stop();save('observed-commands.json',fresh);c=Client(port);process=launch(exe);wait(ready,'fresh-format start');login()
            check(bytes.fromhex(disk()['Commands'][0]['PayloadHex'])==envelope(bytes.fromhex('0000AA39'),function=222),'new-format records with leading zero data are never mistaken for the old four-byte layout')
            (exe.parent/'interface-width-report.json').write_text(json.dumps(dict(checks=len(checks),details=checks),ensure_ascii=False,indent=2),encoding='utf-8')
        except Exception:
            log.flush();print((root/'server.log').read_text('utf-8',errors='replace')[-2000:]);raise
        finally:
            if process and process.poll() is None:process.terminate();process.wait(10)
            receiver.close();log.close()
    print('All',len(checks),'interface-width migration checks passed. Loopback only.')

if __name__=='__main__':main()
