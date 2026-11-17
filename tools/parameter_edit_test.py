"""Single-parameter editing preserves identity and bindings, and changes real UDP decoding."""
import copy,json,secrets,shutil,socket,subprocess,sys,tempfile
from pathlib import Path
from integration_test import ROOT,Client,free_port,wait,packet,seal,seed_send_monitor
from catalog_import_test import workbook
from decoder_integration_test import upload
def main():
    exe=Path(sys.argv[1]).resolve() if len(sys.argv)>1 else ROOT/'TelemetryMonitor.exe';checks=[]
    def check(ok,label):assert ok,label;checks.append(label);print('PASS',label,flush=True)
    with tempfile.TemporaryDirectory(prefix='parameter-edit-') as scratch:
        root=Path(scratch);shutil.copytree(ROOT/'config',root/'config');shutil.copytree(ROOT/'web',root/'web');seed_send_monitor(root)
        for name in ['decoder.json','continuous-rules.json']:(root/'config'/name).unlink(missing_ok=True)
        settings=json.loads((root/'config/server.json').read_text(encoding='utf-8-sig'));settings.update(BindAddress='127.0.0.1',UdpPort=free_port(socket.SOCK_DGRAM),SourceIPs=['127.0.0.1'],MulticastGroup='',MulticastInterface='',DataTimeoutMs=10000);(root/'config/server.json').write_text(json.dumps(settings),encoding='utf-8');port=free_port();password=secrets.token_urlsafe(18)
        def launch():return subprocess.Popen([str(exe),'--local','--root',str(root),'--web-port',str(port)],stdout=subprocess.DEVNULL,stderr=subprocess.STDOUT)
        process=launch();c=Client(port)
        try:
            def ready():
                try:c.call('/api/auth/me');return True
                except OSError:return False
            wait(ready,'edit server');c.call('/api/auth/setup',dict(username='editqa',name='Edit QA',password=password));c.csrf=c.call('/api/auth/login',dict(username='editqa',password=password))['csrf']
            p=upload(c,'parameters.xlsx',workbook([('主表',[('A1','原参数',0,1,'无符号整数',1),('A2','其他参数',1,1,'无符号整数',1),('PENDING','待补全',2,1,'未知类型',1)])]));c.call('/api/decoders/apply',dict(token=p['token'],revision=p['revision'],mode='replace'));d=c.call('/api/decoders');definition=d['configuration']['Packets'][0];definition.update(Groups=['主表'],Enabled=True,MappingConfirmed=True);c.call('/api/decoders/packet',dict(packet=definition,revision=d['revision']));d=c.call('/api/decoders');second=copy.deepcopy(definition);second.update(Id='second',Name='second',Apid=9);c.call('/api/decoders/packet',dict(packet=second,revision=d['revision']))
            def decoder():return c.call('/api/decoders')
            def get(id='主表/A1'):return c.call('/api/decoders/parameter?id='+__import__('urllib.parse',fromlist=['quote']).quote(id))
            def save(parameter,revision=None,expected=200,csrf=True):return c.call('/api/decoders/parameter',dict(id=parameter['Id'],parameter=parameter,revision=revision or decoder()['revision']),expected=expected,csrf=csrf)
            def rules():return c.call('/api/continuous-rules')
            for row in rules()['rows']:
                if row['rule']['Code']!='A1':continue
                r=dict(row['rule']);r['Name']='原参数' if r['ParameterId'].startswith('legacy::') else '自定义判读名';r['Script']='RANGE=[0 20];';c.call('/api/continuous-rules/save',dict(rule=r,revision=rules()['revision']))
            before=decoder();original=get()['parameter'];original_rules={r['rule']['Id']:r['rule'] for r in rules()['rows']};other=next(p for p in before['configuration']['Catalog']['Parameters'] if p['Code']=='A2');edit=copy.deepcopy(original);edit.update(Name='新参数名',Unit='V',Scale=2.5,Bias=1,Precision=2)
            save(edit,expected=403,csrf=False);save(edit);after=decoder();changed=get()['parameter'];check(changed['Id']==original['Id'] and changed['Code']==original['Code'] and changed['SourceRow']==original['SourceRow'] and changed['Scale']==2.5 and changed['Bias']==1,'single edit changes selected definition and preserves parameter identity and provenance')
            check(after['configuration']['Packets']==before['configuration']['Packets'] and next(p for p in after['configuration']['Catalog']['Parameters'] if p['Code']=='A2')==other,'other parameter and both SPP associations remain unchanged')
            now={r['rule']['Id']:r['rule'] for r in rules()['rows']};check(all(now[id]['ParameterId']==r['ParameterId'] and now[id]['Script']==r['Script'] for id,r in original_rules.items()) and {r['Name'] for r in now.values() if r['Code']=='A1'}=={'新参数名','自定义判读名'},'judgment identities/scripts survive; matching default name follows parameter rename and custom rule name remains')
            save(edit,revision=before['revision'],expected=409);baseline=(root/'config/decoder.json').read_bytes()
            invalid=[dict(Code='NEW'),dict(Group='另一个表'),dict(Type='float32',Length=1),dict(BitStart=7,BitCount=3,Length=1),dict(Length=1.5),dict(Offset=65507),dict(Offset=1e100),dict(EnumRanges=[dict(Min=5,Max=1,Text='错')]),dict(Enums={'1':'a','0x1':'b'})]
            for fields in invalid:bad=copy.deepcopy(changed);bad.update(fields);save(bad,expected=400)
            check((root/'config/decoder.json').read_bytes()==baseline,'stale, identity, type, bit width, fraction, overflow and duplicate enum edits cannot change the file')
            c.call('/api/selection',dict(ids=['legacy::主表/A1','legacy::主表/A2','second::主表/A1']));c.call('/api/control',dict(action='start'));save(changed,expected=409)
            with socket.socket(socket.AF_INET,socket.SOCK_DGRAM) as sender:
                data=packet(1);data[22]=4;data[23]=2;seal(data);sender.sendto(data,('127.0.0.1',settings['UdpPort']));data2=packet(1,apid=9);data2[22]=4;seal(data2);sender.sendto(data2,('127.0.0.1',settings['UdpPort']))
            wait(lambda:c.call('/api/state')['accepted']==2,'edited decoding');rows={r['id']:r for r in c.call('/api/state')['rows']};check(rows['legacy::主表/A1']['value']=='11' and rows['second::主表/A1']['value']=='11' and rows['legacy::主表/A2']['value']=='2','real UDP uses modified scale and bias for every SPP referencing the table while other parameter decoding stays unchanged');c.call('/api/control',dict(action='stop'))
            before=(root/'config/decoder.json').read_bytes();meaning=c.call('/api/decoders/parameter/note',dict(note='0：关闭；1：工作；[2,5]：范围正常'));check(set(meaning['enums'])=={'0','1'} and meaning['ranges'][0]['Min']==2 and (root/'config/decoder.json').read_bytes()==before,'annotation parsing is preview-only and supports enum/range notation')
            edited=get()['parameter'];edited.update(Enums={'0x04':'工作'},EnumRanges=[dict(Min=20,Max=30,Text='区间')],Note='4：工作');save(edited);check(get()['parameter']['Enums']=={'4':'工作'},'hex enum keys are normalized without changing their raw-code meaning')
            trial=c.call('/api/decoders/try',dict(packetId='legacy',hex=data.hex(' ')));check(next(p for p in trial['rows'] if p['code']=='A1')['value']=='工作','updated enum annotations affect decoding')
            bits=get()['parameter'];bits.update(BitStart=7,BitCount=2,Length=2,Scale=2,Bias=1,Enums={},EnumRanges=[]);save(bits);data[22]=1;data[23]=128;seal(data);trial=c.call('/api/decoders/try',dict(packetId='legacy',hex=data.hex(' ')));check(next(p for p in trial['rows'] if p['code']=='A1')['value']=='7' and get()['parameter']['CrossByte'],'edited cross-byte bit location follows existing B0/MSB mapping')
            pending=get('主表/PENDING')['parameter'];pending.update(Type='uint',Supported=True,Diagnostic='');save(pending);sheet=decoder()['configuration']['Catalog']['Sheets'][0];check(sheet['Supported']==3 and not get('主表/PENDING')['parameter']['Diagnostic'],'an incomplete imported parameter can be fixed and sheet completeness updates')
            snapshot=decoder()['configuration'];process.terminate();process.wait(timeout=6);process=launch();c=Client(port);wait(ready,'edit restart');c.csrf=c.call('/api/auth/login',dict(username='editqa',password=password))['csrf'];check(decoder()['configuration']==snapshot,'edited parameter configuration persists exactly across restart')
        finally:
            if process.poll() is None:process.terminate();process.wait(timeout=6)
    print('All',len(checks),'parameter editing checks passed.')
if __name__=='__main__':main()
