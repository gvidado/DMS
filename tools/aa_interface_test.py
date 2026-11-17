"""0xAA uses two interface bytes; migration, matching, TC and loopback sending."""
import csv,io,json,secrets,shutil,socket,struct,subprocess,sys,tempfile,urllib.request
from pathlib import Path
from integration_test import ROOT,Client,free_port,wait
from runtime_benchmark import seed

def envelope(data,function=0xAA,interface=92,width=None):
    width=width or (2 if function==0xAA else 4)
    raw=struct.pack('<4sIHIIHH',bytes.fromhex('EB90EB90'),len(data)+18+width,170,1,1,0,function)
    raw+=struct.pack('<H' if width==2 else '<I',interface)+data
    return raw+struct.pack('<I',sum(raw))

def main():
    exe=Path(sys.argv[1]).resolve();checks=[]
    def check(ok,label):assert ok,label;checks.append(label);print('PASS',label,flush=True)
    with tempfile.TemporaryDirectory(dir=ROOT/'test-output',prefix='aa-interface-') as scratch:
        root=Path(scratch);seed(root,per_packet=2,packets=1)
        for name in ['web','native']:shutil.copytree(ROOT/name,root/name)
        def save(name,value):(root/'config'/name).write_text(json.dumps(value),encoding='utf-8')
        cfg=json.loads((root/'config/server.json').read_text('utf-8-sig'));cfg['UdpPort']=free_port(socket.SOCK_DGRAM);save('server.json',cfg)
        original=[envelope(b'\0\0\xA1\x39',width=4),envelope(b'\xCA\xFE\xBA\xBE',interface=52,width=4),
                  envelope(b'\x99\x99',interface=65536,width=4),envelope(b'\xAA\x01',0xAB,0xFFFFFFFF),
                  envelope(b'\xAA\x02',0xDD),envelope(b'\x11\x22',0x1553,32),b'\xD1\xD2']
        # Captured AA is framed but not marked STC; it also needs old-width inference.
        entries=[dict(Number=i,Name='保留名称'+str(i),PayloadHex=data.hex(' '),STC=i not in [2,7],InterfaceBytes=0 if i in [2,7] else 4,
                      DestinationIP='127.0.0.2',DestinationIPs=['127.0.0.2'],Categories=[dict(Kind='BC' if i==6 else 'ZK',Code='BC2001' if i==6 else 'ZK'+str(i).zfill(4),DestinationIPs=['127.0.0.2'])]) for i,data in enumerate(original,1)]
        save('observed-commands.json',dict(SchemaVersion=1,InterfaceWidthRule=3,SourceIP='127.0.0.1',NextNumber=8,Commands=entries))
        save('tc-bindings.json',dict(SchemaVersion=1,Revision='keep-tc',Bindings=[dict(Number=1,Code='OLD_AA')]))
        sid='a'*32;jid='b'*32
        save('sequences.json',dict(SchemaVersion=1,Revision='keep-sequences',DefaultTimeoutSeconds=4,NextJudgeNumber=2,NextExecuteNumber=2,Sequences=[
            dict(Id=sid,Code='XLZ0001',Name='AA自动发送',Mode='execute',SourceIP='127.0.0.1',Rows=[dict(Row=2,Command='ZK0001',Expected='.2')]),
            dict(Id=jid,Code='XLP0001',Name='AA监视判读',Mode='judge',SourceIP='127.0.0.1',Rows=[dict(Row=2,Command='ZK0001')])]))
        rules=json.loads((root/'config/continuous-rules.json').read_text('utf-8'))
        for rule,code in zip(rules['Rules'],['ZK0001','ZK0004']):rule['Script']='InitRegion this.state=0; EndInitRegion StateRegion if(TC.'+code+') then this.state=this.state+1; end EndStateRegion ResultRegion RET=Normal; EndResultRegion'
        save('continuous-rules.json',rules)
        receiver=socket.socket(socket.AF_INET,socket.SOCK_DGRAM);receiver.bind(('127.0.0.2',0));receiver.settimeout(3)
        source=free_port(socket.SOCK_DGRAM);save('command-send.json',dict(SourceIP='127.0.0.1',Routes=[dict(DestinationIP='127.0.0.2',SourcePort=source,DestinationPort=receiver.getsockname()[1])]))
        references=(root/'config/tc-bindings.json').read_bytes();port=free_port();c=Client(port);password=secrets.token_urlsafe(18);process=None;log=(root/'server.log').open('wb')
        def launch():return subprocess.Popen([str(exe),'--local','--root',str(root),'--web-port',str(port)],stdout=log,stderr=subprocess.STDOUT,creationflags=subprocess.CREATE_NO_WINDOW)
        def ready():
            try:c.call('/api/auth/me');return True
            except OSError:return False
        def login():c.csrf=c.call('/api/auth/login',dict(username='aaqa',password=password))['csrf']
        def disk():return json.loads((root/'config/observed-commands.json').read_text('utf-8-sig'))
        def preview(data,format):
            request=urllib.request.Request(c.base+'/api/send-monitor/commands/preview?format='+format,data=data,headers={'Content-Type':'application/octet-stream','X-CSRF-Token':c.csrf})
            return json.loads(c.opener.open(request).read())
        def cancel(p):c.call('/api/send-monitor/commands/preview/cancel',dict(token=p['token']))
        def states():return [r['currentState'] for r in c.call('/api/continuous-rules')['rows']]
        def start(id):return c.call('/api/sequence/control',dict(action='start',id=id,revision=c.call('/api/sequence/config')['revision'],runName='AA联调',requestId=secrets.token_hex(16)))['runId']
        def status(run):return c.call('/api/sequence/state?runId='+run)['runtime']
        try:
            process=launch();wait(ready,'AA server');c.call('/api/auth/setup',dict(username='aaqa',name='AA接口验证',password=password));login()
            stored=disk();rows={e['Number']:e for e in stored['Commands']}
            check(stored['InterfaceWidthRule']==4 and len(rows)==7,'interface rule 4 retains every existing command')
            check(bytes.fromhex(rows[1]['PayloadHex'])==envelope(b'\0\0\xA1\x39') and rows[1]['InterfaceBytes']==2,'legacy four-byte AA STC becomes two bytes without removing leading zero data')
            check(bytes.fromhex(rows[2]['PayloadHex'])==envelope(b'\xCA\xFE\xBA\xBE',interface=52) and rows[2]['InterfaceBytes']==2 and not rows[2]['STC'],'legacy captured AA also migrates using the previous format rule')
            check(bytes.fromhex(rows[3]['PayloadHex'])==original[2] and rows[3]['InterfaceBytes']==4 and '65535' in rows[3]['FormatIssue'],'AA overflow is preserved and blocked instead of silently truncated')
            check(all(bytes.fromhex(rows[i]['PayloadHex'])==original[i-1] for i in [4,5,6,7]),'0xAB, 0xDD, 0x1553 and ordinary UDP retain their complete stored bytes')
            check(all(rows[i]['Name']==entries[i-1]['Name'] and rows[i]['Categories']==entries[i-1]['Categories'] for i in rows) and (root/'config/tc-bindings.json').read_bytes()==references,'names, category codes, destinations and manual TC bindings remain intact')
            for function in ['170','0xAA']:
                p=preview(('<R><STCVars><STCVar name="AA预览" bujianid="'+function+'" gongnengid="65535" datastr="009988"/></STCVars></R>').encode(),'stc-xml');r=p['xmlRows'][0]
                check(not p['errors'] and r['interfaceBytes']==2 and bytes.fromhex(r['packetHex'])==envelope(b'\0\x99\x88',interface=65535),'XML '+function+' creates the same two-byte AA interface');cancel(p)
            p=preview(json.dumps(dict(name='AA手动',bujianid='0xAA',gongnengid='0x1234',datastr='AA39',kind='ZK',destinationIP='127.0.0.2')).encode(),'stc-manual')
            check(not p['errors'] and p['xmlRows'][0]['interfaceBytes']==2 and bytes.fromhex(p['xmlRows'][0]['packetHex'])==envelope(b'\xAA\x39',interface=0x1234),'manual AA builds the same validated two-byte layout');cancel(p)
            p=preview(json.dumps(dict(bujianid='170',gongnengid='65536',datastr='AA39',kind='ZK',destinationIP='127.0.0.2')).encode(),'stc-manual')
            check(bool(p['errors']) and not p['canApply'],'new AA interfaces over 65535 cannot enter the library');cancel(p)
            bad=c.call('/api/command-send/preview',dict(number=3,code='ZK0003'),expected=400)
            check('65535' in bad['error'],'legacy AA overflow cannot bypass validation through active sending')
            c.call('/api/control',dict(action='start'));c.call('/api/continuous-rules/control',dict(action='start'))
            waiting=start(jid);wait(lambda:status(waiting)['running'],'waiting judgment');execution=start(sid)
            raw,origin=receiver.recvfrom(65535)
            check(raw==envelope(b'\0\0\xA1\x39') and origin[1]==source,'sequence sends actual AA UDP with two interface bytes from the configured source port')
            wait(lambda:not status(waiting)['running'] and not status(execution)['running'],'both sequences finish');wait(lambda:states()==[1,0],'AA TC dispatch')
            check(status(waiting)['abnormal']==status(execution)['abnormal']==0 and c.call('/api/send-monitor/list')['records'][0]['commandCode']=='ZK0001','AA capture matches the library and completes its own TC/sequence judgment')
            for number,code,expected,offset in [(4,'ZK0004',original[3],27),(2,'ZK0002',envelope(b'\xCA\xFE\xBA\xBE',interface=52),25),(7,'ZK0007',b'\xD1\xD2',0)]:
                plan=c.call('/api/command-send/preview',dict(number=number,code=code));result=c.call('/api/command-send/send',dict(token=plan['token']));raw,_=receiver.recvfrom(65535)
                check(result['ok'] and raw==expected and plan['dataContentStart']==offset,code+' preview and actual send agree on interface width and content boundary')
                wait(lambda:c.call('/api/send-monitor/list')['records'][0]['commandCode']==code,'matching '+code)
            wait(lambda:states()==[1,1],'non-AA TC dispatch');check(states()==[1,1],'AA and non-AA commands trigger only their corresponding TC event')
            c.call('/api/control',dict(action='stop'))
            library_before=(root/'config/observed-commands.json').read_bytes();process.terminate();process.wait(10);process=launch();wait(ready,'restart');login()
            check((root/'config/observed-commands.json').read_bytes()==library_before,'restart does not shrink AA data a second time or rewrite the migrated library')
        except Exception:
            log.flush();(exe.parent/'aa-server-error.log').write_bytes((root/'server.log').read_bytes());raise
        finally:
            if process is not None and process.poll() is None:process.terminate();process.wait(10)
            log.close();receiver.close()
    (exe.parent/'aa-interface-report.json').write_text(json.dumps(dict(checks=len(checks),details=checks),ensure_ascii=False,indent=2),encoding='utf-8')
    print('All',len(checks),'AA interface checks passed; loopback only.')

if __name__=='__main__':main()
