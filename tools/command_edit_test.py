"""Command metadata editing, reference migration, rollback and restart recovery. Loopback only."""
import base64,csv,ctypes,io,json,secrets,shutil,socket,subprocess,sys,tempfile
from pathlib import Path
from integration_test import ROOT,Client,free_port,wait,seed_send_monitor

def main():
    exe=Path(sys.argv[1]).resolve();checks=[]
    def check(value,label):
        assert value,label
        checks.append(label);print('PASS',label,flush=True)
    with tempfile.TemporaryDirectory(prefix='command-edit-') as scratch:
        root=Path(scratch);config=root/'config';shutil.copytree(ROOT/'config',config);shutil.copytree(ROOT/'web',root/'web');seed_send_monitor(root)
        for name in ['decoder.json','continuous-rules.json','tc-bindings.json','sequences.json','observed-commands.json']:(config/name).unlink(missing_ok=True)
        def save(name,value):(config/name).write_text(json.dumps(value,ensure_ascii=False),encoding='utf-8')
        def read(name):return json.loads((config/name).read_text(encoding='utf-8-sig'))
        entries=[dict(Number=n,Name=name,DestinationIP=ips[0],DestinationIPs=ips,PayloadHex=payload,Length=len(payload.split()),FirstSeenUtc='2026-11-04T00:00:00Z') for n,name,ips,payload in [(1,'原指令',['192.168.1.113','192.168.1.111'],'00 AA'),(2,'其他指令',['192.168.1.113'],'00 BB'),(3,'未分类',['127.0.0.2'],'00 CC'),(4,'KC指令',['192.168.1.137'],'00 DD')]]
        save('observed-commands.json',dict(SchemaVersion=1,SourceIP='127.0.0.1',IdentityRule='udp-payload',NextNumber=5,Commands=entries))
        script='// TC.BC0001 comment stays\nResultRegion\n if TC.BC0001 || TC.BC00010 then RET=Normal; else RET=Error; end\nEndResultRegion'
        save('continuous-rules.json',dict(SchemaVersion=1,Revision='rules-before',Rules=[dict(Id='edit-rule',Code='A1',Name='测试参数',Level='一般',Script=script,ParameterId='工仪全程/A1',Enabled=False,Bindings={})]))
        sequence_id='1'*32
        save('sequences.json',dict(SchemaVersion=1,Revision='sequence-before',DefaultTimeoutSeconds=30,Sequences=[dict(Id=sequence_id,Name='编辑引用测试',Mode='judge',SourceIP='127.0.0.1',Rows=[dict(Row=n,Keyword='',Description='TC.BC0001 stays in description',Command=code,Condition='',Mark='',Expected='',Timeout='') for n,code in enumerate(['BC0001','TC.BC0001','BC00010','K001'],1)])]))
        port=free_port();password=secrets.token_urlsafe(18);process=None;c=Client(port)
        def start():return subprocess.Popen([str(exe),'--local','--root',str(root),'--web-port',str(port)],stdout=subprocess.DEVNULL,stderr=subprocess.STDOUT,creationflags=subprocess.CREATE_NO_WINDOW)
        def ready():
            try:c.call('/api/auth/me');return True
            except OSError:return False
        def login():c.csrf=c.call('/api/auth/login',dict(username='editqa',password=password))['csrf']
        def listing():return c.call('/api/send-monitor/commands/list')
        def edit(number,old,code,name='新名称',revision=None,expected=200,csrf=True):
            return c.call('/api/send-monitor/commands/edit',dict(number=str(number),originalCode=old,code=code,name=name,revision=str(listing()['revision'] if revision is None else revision)),expected=expected,csrf=csrf)
        try:
            process=start();wait(ready,'command edit server');c.call('/api/send-monitor/commands/edit',{},401)
            c.call('/api/auth/setup',dict(username='editqa',name='Edit QA',password=password));login()
            tc=c.call('/api/continuous-rules/tc');c.call('/api/continuous-rules/tc',dict(number=1,code='K001',revision=tc['revision']))
            tc=c.call('/api/continuous-rules/tc');c.call('/api/continuous-rules/tc',dict(number=2,code='BC0099',revision=tc['revision']))
            original=read('observed-commands.json');bindings=read('tc-bindings.json');before_revision=listing()['revision']
            result=edit(1,'BC0001','BC0042','  修改后的指令  ')
            after=read('observed-commands.json');entry=next(e for e in after['Commands'] if e['Number']==1)
            check(entry['Name']=='修改后的指令' and {x['Code'] for x in entry['Categories']}=={'BC0042','PSS001'},'rename changes only selected category code and the shared command name')
            check(all(entry[k]==original['Commands'][0][k] for k in ['Number','PayloadHex','DestinationIP','DestinationIPs','Length','FirstSeenUtc']) and after['NextNumber']==original['NextNumber'],'stable ID, complete payload, destinations and original capture metadata stay unchanged')
            check(result['renamedRules']==1 and result['renamedSequenceRows']==2,'edit reports exact continuous-rule and sequence-row migration counts')
            rule=next(r for r in read('continuous-rules.json')['Rules'] if r['Id']=='edit-rule')
            check(rule['Script']==script.replace('if TC.BC0001 ', 'if TC.BC0042 ') and rule['Description']=='','only complete TC identifier tokens change; comments and longer identifiers are preserved')
            rows=read('sequences.json')['Sequences'][0]['Rows']
            check([r['Command'] for r in rows]==['BC0042','TC.BC0042','BC00010','K001'] and rows[0]['Description']=='TC.BC0001 stays in description','both sequence command spellings migrate without changing descriptions or manual codes')
            check(bindings==read('tc-bindings.json') and c.call('/api/continuous-rules/tc/detail?number=1')['name']=='修改后的指令','manual TC binding remains attached to the same stable command')
            check(read('continuous-rules.json')['Revision']!='rules-before' and read('sequences.json')['Revision']!='sequence-before','changed references advance configuration revisions to invalidate stale editors')
            stable={n:(config/n).read_bytes() for n in ['observed-commands.json','continuous-rules.json','sequences.json']}
            edit(1,'BC0042','BC0002',expected=400);edit(1,'BC0042','PSS042',expected=400);edit(1,'BC0042','BC0000',expected=400);edit(1,'BC0042','BC0043',name=' ',expected=400)
            edit(1,'BC0042','BC0099',expected=400);edit(1,'BC0042','BC0043',revision=before_revision,expected=409);edit(1,'BC0042','BC0043',expected=403,csrf=False)
            check(all((config/n).read_bytes()==b for n,b in stable.items()),'duplicate, wrong-prefix, zero, empty-name, manual-code collision, stale revision and missing CSRF cannot modify any file')
            edit(1,'BC0042','BC0042',name='只改名称');check(read('continuous-rules.json')['Revision']==json.loads(stable['continuous-rules.json'])['Revision'],'name-only edits leave rule and sequence references untouched')
            edit(3,'','','未分类改名');edit(3,'','BC0008',expected=400);check(next(r for r in listing()['rows'] if r['number']==3)['name']=='未分类改名','unclassified commands can be renamed without inventing an unrelated category')
            edit(1,'PSS001','PSS020');edit(4,'KC0001','KC0020');check({'BC0042','PSS020','KC0020'}.issubset({r['code'] for r in listing()['rows']}),'each category code can be edited independently')
            raw=c.opener.open(c.base+'/api/send-monitor/commands/export.csv').read().decode('utf-8-sig');export=list(csv.DictReader(io.StringIO(raw)))
            check(any(r['指令编号']=='BC0042' and r['指令名称']=='新名称' for r in export),'CSV export uses the edited names and codes')
            settings=c.call('/api/config');settings.update(BindAddress='127.0.0.1',UdpPort=free_port(socket.SOCK_DGRAM),SourceIPs=['127.0.0.1'],MulticastGroup='',MulticastInterface='');c.call('/api/config',settings)
            c.call('/api/control',dict(action='start'));edit(1,'BC0042','BC0043',expected=409);c.call('/api/control',dict(action='stop'));check(True,'live listening blocks metadata changes')
            stable={n:(config/n).read_bytes() for n in ['observed-commands.json','continuous-rules.json','sequences.json']}
            kernel=ctypes.WinDLL('kernel32',use_last_error=True);kernel.CreateFileW.argtypes=[ctypes.c_wchar_p,ctypes.c_uint32,ctypes.c_uint32,ctypes.c_void_p,ctypes.c_uint32,ctypes.c_uint32,ctypes.c_void_p];kernel.CreateFileW.restype=ctypes.c_void_p;kernel.CloseHandle.argtypes=[ctypes.c_void_p]
            handle=kernel.CreateFileW(str(config/'observed-commands.json'),0x80000000,1,None,3,0,None);assert handle not in (None,ctypes.c_void_p(-1).value)
            try:edit(1,'BC0042','BC0043',expected=500)
            finally:kernel.CloseHandle(handle)
            check(all((config/n).read_bytes()==b for n,b in stable.items()) and not (config/'command-edit.pending.json').exists(),'a failed library write rolls back earlier rule and sequence writes and removes the recovery journal')
            edit(1,'BC0042','BC0043');check(listing()['rows'] and 'TC.BC0043' in read('continuous-rules.json')['Rules'][0]['Script'],'editing still works after a rolled-back failure')
            stable={n:(config/n).read_bytes() for n in ['observed-commands.json','continuous-rules.json','sequences.json','tc-bindings.json']}
            process.terminate();process.wait(timeout=6);process=start();c=Client(port);wait(ready,'edit restart');login()
            check(all((config/n).read_bytes()==b for n,b in stable.items()) and any(r['code']=='BC0043' for r in listing()['rows']),'edited library, references and manual bindings persist across server restart')
            # Reproduce interruption after the reference file has been replaced but before commit.
            process.terminate();process.wait(timeout=6)
            before=stable['continuous-rules.json'];after=before.replace(b'TC.BC0043',b'TC.BC0044');(config/'continuous-rules.json').write_bytes(after)
            save('command-edit.pending.json',[dict(Name='continuous-rules.json',Before=base64.b64encode(before).decode(),After=base64.b64encode(after).decode())])
            process=start();c=Client(port);wait(ready,'recovery restart');login()
            check((config/'continuous-rules.json').read_bytes()==before and not (config/'command-edit.pending.json').exists(),'startup recovers interrupted cross-file edits before loading configurations')
        finally:
            if process and process.poll() is None:process.terminate();process.wait(timeout=6)
    print('All',len(checks),'command-edit checks passed. No device traffic sent.')
if __name__=='__main__':main()
