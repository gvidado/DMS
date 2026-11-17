"""Upgrade V0.10.21 two-byte 0xDD records without changing their data or bindings."""
import csv,io,json,secrets,shutil,socket,struct,subprocess,sys,tempfile,urllib.request
from pathlib import Path
from integration_test import ROOT,Client,free_port,wait
from runtime_benchmark import seed
from stc_xml_import_test import envelope

def short(data,interface=92):
    b=struct.pack('<4sIHIIHHH',bytes.fromhex('EB90EB90'),len(data)+20,170,1,1,0,221,interface)+data
    return b+struct.pack('<I',sum(b))

def main():
    exe,old=map(lambda s:Path(s).resolve(),sys.argv[1:3]);checks=[]
    def check(ok,label):assert ok,label;checks.append(label);print('PASS',label,flush=True)
    with tempfile.TemporaryDirectory(dir=ROOT/'test-output',prefix='dd-width-') as temporary:
        root=Path(temporary);seed(root,per_packet=1,packets=1);shutil.copytree(ROOT/'web',root/'web')
        payloads=[short(bytes.fromhex('0000AA39')),short(b'\xAA',65535),envelope(bytes.fromhex('7788'),interface=0x12345678)]
        entries=[dict(Number=i,Name='保留DD名称'+str(i),STC=True,InterfaceBytes=2 if i<3 else 4,FormatIssue='' if i<3 else '旧接口类型超过65535',DestinationIP='127.0.0.2',DestinationIPs=['127.0.0.2'],PayloadHex=raw.hex(' '),Categories=[dict(Kind='ZK',Code='ZK'+str(i).zfill(4),DestinationIPs=['127.0.0.2'])]) for i,raw in enumerate(payloads,1)]
        def save(name,value):(root/'config'/name).write_text(json.dumps(value),encoding='utf-8')
        save('observed-commands.json',dict(SchemaVersion=1,InterfaceWidthRule=1,SourceIP='127.0.0.1',NextNumber=4,Commands=entries));save('tc-bindings.json',dict(Revision='initial',Bindings=[]))
        receiver=socket.socket(socket.AF_INET,socket.SOCK_DGRAM);receiver.bind(('127.0.0.2',0));receiver.settimeout(3);source=free_port(socket.SOCK_DGRAM)
        save('command-send.json',dict(SourceIP='127.0.0.1',Routes=[dict(DestinationIP='127.0.0.2',SourcePort=source,DestinationPort=receiver.getsockname()[1])]))
        port=free_port();c=Client(port);password=secrets.token_urlsafe(18);process=None;log=(root/'server.log').open('wb')
        def launch(binary):return subprocess.Popen([str(binary),'--local','--root',str(root),'--web-port',str(port)],stdout=log,stderr=subprocess.STDOUT,creationflags=subprocess.CREATE_NO_WINDOW)
        def ready():
            try:c.call('/api/auth/me');return True
            except OSError:return False
        def login():c.csrf=c.call('/api/auth/login',dict(username='ddqa',password=password))['csrf']
        def stop():process.terminate();process.wait(10)
        try:
            process=launch(old);wait(ready,'previous DD version');c.call('/api/auth/setup',dict(username='ddqa',name='DD转换验证',password=password));login()
            binding=c.call('/api/command-panels/bind',dict(number=1,code='ZK0001'))['button'];binding['Id']='dd-button'
            panel=c.call('/api/command-panels')['configuration'];panel['Screens'][0]['Buttons']=[binding];c.call('/api/command-panels',dict(configuration=panel))
            tc=c.call('/api/continuous-rules/tc');c.call('/api/continuous-rules/tc',dict(number=1,code='DDTEST',revision=tc['revision']));tc_before=(root/'config/tc-bindings.json').read_bytes()
            c.call('/api/command-send/preview',dict(number=3,code='ZK0003'),expected=400)
            check(True,'real V0.10.21 stores short DD frames and blocks a retained 32-bit DD value')
            stop();c=Client(port);process=launch(exe);wait(ready,'updated DD version');login();disk=json.loads((root/'config/observed-commands.json').read_text('utf-8-sig'))
            check(disk['InterfaceWidthRule']==2 and len(disk['Commands'])==3,'new layout revision migrates all DD records')
            expected=[envelope(bytes.fromhex('0000AA39')),envelope(b'\xAA',interface=65535),payloads[2]]
            check(all(bytes.fromhex(row['PayloadHex'])==expected[i] and row['InterfaceBytes']==4 and not row['FormatIssue'] for i,row in enumerate(disk['Commands'])),'short, one-byte-data and previously blocked DD records all become valid four-byte frames')
            check(all(row['Name']==entries[i]['Name'] and row['Number']==entries[i]['Number'] and row['Categories']==entries[i]['Categories'] for i,row in enumerate(disk['Commands'])),'DD conversion preserves names, numbers and categories')
            check(c.call('/api/command-panels')['bindings'][0]['available'] and (root/'config/tc-bindings.json').read_bytes()==tc_before,'existing panel hash and manual TC binding remain valid')
            for i,raw in enumerate(expected,1):
                p=c.call('/api/command-send/preview',dict(number=i,code='ZK'+str(i).zfill(4)))
                check(bytes.fromhex(p['hex'])==raw and p['dataContentStart']==27 and p['interfaceTypeBytes']==4,'DD preview uses byte 27 for record '+str(i))
                c.call('/api/command-send/send',dict(token=p['token']));received,origin=receiver.recvfrom(65535);check(received==raw and origin==('127.0.0.1',source),'DD send preserves every data byte for record '+str(i))
            csv_packet=envelope(bytes.fromhex('6677'),interface=65536);csv_text=io.StringIO();csv.writer(csv_text).writerows([['指令编号','指令名称','指令类别','来源IP','目的IP','完整UDP载荷HEX','指令格式'],['ZK9000','旧CSV中的DD指令','ZK','127.0.0.1','127.0.0.2',csv_packet.hex(' '),'STC-LEGACY4']])
            request=urllib.request.Request(c.base+'/api/send-monitor/commands/preview',data=csv_text.getvalue().encode('utf-8'),headers={'Content-Type':'application/octet-stream','X-CSRF-Token':c.csrf});plan=json.loads(c.opener.open(request).read());assert plan['canApply'];c.call('/api/send-monitor/commands/apply',dict(token=plan['token']))
            imported=c.call('/api/command-send/preview',dict(number=4,code='ZK9000'));check(bytes.fromhex(imported['hex'])==csv_packet,'old CSV legacy-four-byte DD entries are now valid under the new rule')
            before=(root/'config/observed-commands.json').read_bytes();stop();c=Client(port);process=launch(exe);wait(ready,'repeat DD startup');login()
            check((root/'config/observed-commands.json').read_bytes()==before and c.call('/api/command-panels')['bindings'][0]['available'],'restarting cannot add a second pair of zero interface bytes')
            (exe.parent/'dd-width-report.json').write_text(json.dumps(dict(checks=len(checks),details=checks),ensure_ascii=False,indent=2),encoding='utf-8')
        except Exception:
            log.flush();print((root/'server.log').read_text('utf-8',errors='replace')[-1500:]);raise
        finally:
            if process and process.poll() is None:process.terminate();process.wait(10)
            receiver.close();log.close()
    print('All',len(checks),'DD upgrade checks passed. Loopback only.')

if __name__=='__main__':main()
