"""V0.9.1 outer source, mappings, judgments, histories and legacy migration on loopback only."""
import copy,json,secrets,shutil,socket,struct,subprocess,sys,tempfile,urllib.request,urllib.parse
from pathlib import Path
from integration_test import ROOT,Client,free_port,wait,packet,seal,seed_send_monitor
from catalog_import_test import workbook
from sequence_test import xlsx
from continuous_rules_test import workbook as rulebook

def main():
    exe=Path(sys.argv[1]).resolve();checks=[]
    def check(ok,label):assert ok,label;checks.append(label);print('PASS',label,flush=True)
    with tempfile.TemporaryDirectory(prefix='namespace-http-',dir=ROOT/'test-output') as scratch:
        root=Path(scratch);config=root/'config';shutil.copytree(ROOT/'config',config);shutil.copytree(ROOT/'web',root/'web');seed_send_monitor(root)
        for name in ['decoder.json','continuous-rules.json','tc-bindings.json','sequences.json','observed-commands.json']:(config/name).unlink(missing_ok=True)
        settings=json.loads((config/'server.json').read_text(encoding='utf-8-sig'));udp_port=free_port(socket.SOCK_DGRAM);settings.update(BindAddress='127.0.0.1',UdpPort=udp_port,SourceIPs=['127.0.0.1'],MulticastGroup='',MulticastInterface='',MappingConfirmed=True,DataTimeoutMs=60000);(config/'server.json').write_text(json.dumps(settings),encoding='utf-8')
        port=free_port();c=Client(port);password=secrets.token_urlsafe(18);process=None;sender=socket.socket(socket.AF_INET,socket.SOCK_DGRAM)
        def start():return subprocess.Popen([str(exe),'--local','--root',str(root),'--web-port',str(port)],stdout=subprocess.DEVNULL,stderr=subprocess.STDOUT,creationflags=subprocess.CREATE_NO_WINDOW)
        def ready():
            try:c.call('/api/auth/me');return True
            except OSError:return False
        def login():c.csrf=c.call('/api/auth/login',dict(username='namespaceqa',password=password))['csrf']
        def upload(path,data):
            request=urllib.request.Request(c.base+'/api/'+path,data=data,headers={'Content-Type':'application/octet-stream','X-CSRF-Token':c.csrf})
            try:return json.loads(c.opener.open(request,timeout=20).read())
            except urllib.error.HTTPError as e:raise AssertionError(e.read().decode('utf-8')) from e
        def decoder():return c.call('/api/decoders')
        def rules():return c.call('/api/continuous-rules')
        def rule(code):return next(r for r in rules()['rows'] if r['rule']['Code']==code and not r['rule'].get('MigrationPending'))
        def put(packet,expected=200):return c.call('/api/decoders/packet',dict(packet=packet,revision=decoder()['revision']),expected=expected)
        def set_rule(code,script,enabled=True):
            r=rule(code)['rule'];r.update(Script=script,Enabled=enabled,SourceIP='127.0.0.1');c.call('/api/continuous-rules/save',dict(rule=r,revision=rules()['revision']))
        def history(reference):return c.call('/api/telemetry/history?'+urllib.parse.urlencode(dict(reference=reference,sourceIP='127.0.0.1')))
        def data(source,seq,a1=5,a2=1):
            raw=packet(seq);struct.pack_into('>HHH',raw,0,source,0,len(raw)-7);raw[22:25]=bytes([a1,a2,3]);seal(raw);sender.sendto(raw,('127.0.0.1',udp_port));return raw
        try:
            process=start();wait(ready,'namespace server');c.call('/api/auth/setup',dict(username='namespaceqa',name='来源测试',password=password));login()
            incoming=workbook([('主表',[('A1','参数一',0,1,'无符号整数',1),('A2','参数二',1,1,'无符号整数',1)]),('唯一E',[('U1','唯一参数',2,1,'无符号整数',1)])])
            p=upload('decoders/preview?filename=telemetry.xlsx',incoming);c.call('/api/decoders/apply',dict(token=p['token'],revision=p['revision'],mode='replace'))
            check(rules()['total']==0 and not c.call('/api/catalog'),'importing decoder definitions alone creates neither runtime mappings nor default rules')
            original=copy.deepcopy(decoder()['configuration']['Catalog']['Parameters'])
            unknown=copy.deepcopy(decoder()['configuration']['Packets'][0]);unknown.update(Id='unknown',Name='未设来源',Enabled=False,Groups=['主表'],MappingConfirmed=False)
            put(unknown);check(rules()['total']==0,'associating a table before setting the outer source still creates no variables or rules')
            unknown.update(Enabled=True);check('数据源' in put(unknown,400)['error'],'enabling a packet without its outer source is rejected')
            e=copy.deepcopy(unknown);e.update(Id='source-e',Name='E数据',Enabled=True,MappingConfirmed=True,OuterSourceId=0x0802,SourceIP='127.0.0.1',SppOffset=6,Groups=['主表','唯一E']);put(e)
            check({r['rule']['Code'] for r in rules()['rows']}=={'TME.A1','TME.A2','TME.U1'},'source plus table association creates source-qualified default rules')
            check(all(not r['rule']['Enabled'] and r['rule']['Level']=='一般' and 'this.state=0' in r['rule']['Script'] and 'RET=Normal' in r['rule']['Script'] for r in rules()['rows']),'initialized rules retain the complete three-region inactive template')
            b=copy.deepcopy(e);b.update(Id='source-b',Name='B数据',OuterSourceId=0x0202,Groups=['主表']);put(b)
            check({p['Reference'] for p in c.call('/api/catalog')}=={'TME.A1','TME.A2','TME.U1','TMB.A1','TMB.A2'},'identical APID and SPP fields coexist when outer source identifiers differ')
            check(decoder()['configuration']['Catalog']['Parameters']==original,'mapping into both namespaces leaves original decoder definitions unchanged')
            duplicate=copy.deepcopy(e);duplicate.update(Id='duplicate-e',Name='重复E',OuterSourceId=0x0803)
            check('同一来源变量' in put(duplicate,400)['error'],'two enabled mappings cannot overwrite the same public namespace variable')
            actual=upload('decoders/spp/batch/preview?filename=SPP.xlsx',(ROOT.parent/'提供材料/SPP.xlsx').read_bytes())
            sheets={r['sheet']['Sheet']:r['sheet'] for r in actual['rows']}
            check(sheets['Sheet3']['Fields']['OuterSourceId']==0x0202 and sheets['Sheet3']['Fields']['Apid']==10 and sheets['Sheet3']['Fields']['ExpectedLengthField']==1017,'actual new SPP Excel reads hexadecimal source and shifted protocol fields')
            check(sheets['Sheet1']['Fields']['OuterSourceId']==-1 and sheets['Sheet1']['Fields']['Apid']==8 and sheets['Sheet2']['Fields']['Apid']==9,'old sheets still preview with a missing-source diagnostic')
            c.call('/api/decoders/preview/cancel',dict(token=actual['token']))
            set_rule('TME.A1','if(TME.A1.value==5 && TMB.A1.value==8) then RET=Normal; else RET=Error; end')
            set_rule('TMB.A1','if(FRAMES(this.value>0,2,TMB.A1)) then RET=Normal; else RET=Unusual; end')
            set_rule('TME.A2','if(abs(TME.A1.value-TMB.A1.value)==3) then RET=Normal; else RET=Error; end')
            set_rule('TMB.A2','if(this.pre1!=NIL) then RANGE=[0 255]; end')
            c.call('/api/control',dict(action='start'));c.call('/api/continuous-rules/control',dict(action='start'))
            data(0x0802,10,5);data(0x0202,10,8);wait(lambda:c.call('/api/state')['accepted']==2,'two independent source frames');wait(lambda:rule('TME.A1')['result']=='Normal','cross source rule')
            check(rule('TME.A2')['result']=='Normal' and c.call('/api/state')['duplicates']==0,'both sources update independently at the same APID and sequence number; cross-source math executes')
            data(0x0802,10,5);data(0x0202,10,8);wait(lambda:c.call('/api/state')['duplicates']==2,'duplicates');check(c.call('/api/state')['accepted']==2,'duplicate windows are maintained independently for each outer source')
            data(0x0802,11,6);data(0x0202,11,8);wait(lambda:c.call('/api/state')['accepted']==4,'new source frames');wait(lambda:rule('TMB.A1')['result']=='Normal','source frames condition')
            eh,bh=history('TME.A1'),history('TMB.A1');check(eh['count']==2 and bh['count']==2 and eh['slots'][0]['valueText']=='6' and bh['slots'][0]['valueText']=='8' and eh['slots'][1]['valueText']=='5','engineering histories and pre values never mix TME and TMB samples')
            check(rule('TMB.A2')['result']=='Normal','source-qualified history references and this.pre1 work in the rule engine')
            stats={r['id']:r for r in c.call('/api/state')['packetStatistics']};check(stats['source-e']['accepted']==2 and stats['source-b']['accepted']==2 and stats['source-e']['duplicates']==1 and stats['source-b']['duplicates']==1,'per-packet accepted and duplicate counters remain separated')
            c.call('/api/continuous-rules/control',dict(action='stop'))
            before=c.call('/api/state')['accepted'];bad=data(0x0802,12,6);bad[5]-=1;sender.sendto(bad,('127.0.0.1',udp_port));wait(lambda:c.call('/api/state')['invalid']>=1,'outer length check');check(c.call('/api/state')['accepted']<=before+1,'bad outer length is rejected before parameter updates')
            c.call('/api/control',dict(action='stop'))
            sequence=upload('sequence/preview?filename=qualified.xlsx&mode=judge&sourceIP=127.0.0.1&timeout=2',xlsx([('序列',[['','双来源','','TME.A1.value > 0 AND TMB.A1.value > 0','','','']])]))
            check(not sequence['definitions'][0]['errors'],'sequence predicates accept full TME/TMB references and logical operations')
            legacy_seq=upload('sequence/preview?filename=legacy.xlsx&mode=judge&sourceIP=127.0.0.1&timeout=2',xlsx([('旧序列',[['','待选择来源','','TM.A1.value > 0','','','']])]))
            check(any('旧TM引用' in e for e in legacy_seq['definitions'][0]['errors']),'old ambiguous sequence references explain how to choose a new source instead of guessing')
            imported=upload('continuous-rules/preview?filename=qualified.xlsx&sourceIP=127.0.0.1',rulebook([[1,'TME.A1','参数一','一般','否','RET=Normal;'],[2,'TMB.A1','参数一','重要','否','RET=Normal;']]))
            check(imported['accepted']==2,'rule Excel binds both namespaces as separate variables')
            c.call('/api/continuous-rules/preview/cancel',dict(token=imported['token']))
            process.terminate();process.wait(timeout=6)
            def save(name,value):(config/name).write_text(json.dumps(value,ensure_ascii=False),encoding='utf-8')
            old_script='// TM.A1 comment stays\nif(this.value>TM.A2.value) then RET=Normal; else RET=Error; end'
            save('continuous-rules.json',dict(SchemaVersion=1,Revision='legacy-before',Rules=[dict(Id='old-ambiguous',Code='A1',Name='旧自定义规则',ParameterId='主表/A1',SourceIP='127.0.0.1',Level='重要',Enabled=True,Script=old_script),dict(Id='old-unique',Code='U1',Name='唯一规则',ParameterId='唯一E/U1',SourceIP='127.0.0.1',Level='关键',Enabled=False,Script='if(TM.U1.value>0) then RET=Normal; else RET=Error; end')]))
            process=start();c=Client(port);wait(ready,'legacy migration server');login()
            pending=next(r['rule'] for r in rules()['rows'] if r['rule']['Id']=='old-ambiguous')
            check(pending['MigrationPending'] and not pending['Enabled'] and pending['MigrationOriginalEnabled'] and pending['Script']==old_script,'ambiguous old rules retain their contents and enabled intent but cannot execute before manual migration')
            unique=next(r['rule'] for r in rules()['rows'] if r['rule']['Id']=='old-unique');check(unique['Code']=='TME.U1' and 'TME.U1.value' in unique['Script'] and not unique['MigrationPending'],'a unique existing mapping can be translated without choosing between sources')
            choices=c.call('/api/continuous-rules/migration?id=old-ambiguous');check({t['reference'] for t in choices['targets']}=={'TME.A1','TMB.A1'},'manual migration lists both actual SPP mappings')
            stable=(config/'continuous-rules.json').read_bytes();preview=c.call('/api/continuous-rules/migration-preview',dict(id='old-ambiguous',targets=[t['id'] for t in choices['targets']]))
            check((config/'continuous-rules.json').read_bytes()==stable and not any(r['error'] for r in preview['rows']),'previewing or cancelling migration never modifies saved rules')
            check(all('// TM.A1 comment stays' in r['rule']['Script'] and r['reference'].split('.')[0]+'.A2.value' in r['rule']['Script'] for r in preview['rows']),'migration rewrites references for each chosen source while preserving comments')
            body=dict(id=preview['id'],revision=preview['revision'],decoderRevision=preview['decoderRevision'],rules=[r['rule'] for r in preview['rows']]);badbody=copy.deepcopy(body);badbody['rules'][1]['Script']='broken =';c.call('/api/continuous-rules/migration-apply',badbody,expected=400)
            check((config/'continuous-rules.json').read_bytes()==stable,'one invalid migration target prevents partial saves of all targets')
            stale=copy.deepcopy(body);stale['decoderRevision']='stale';c.call('/api/continuous-rules/migration-apply',stale,expected=409);check((config/'continuous-rules.json').read_bytes()==stable,'stale decoder revisions cannot apply migration')
            c.call('/api/continuous-rules/migration-apply',body);migrated=[r['rule'] for r in rules()['rows'] if r['rule']['Name']=='旧自定义规则'];check(len(migrated)==2 and {r['Code'] for r in migrated}=={'TME.A1','TMB.A1'} and all(not r['Enabled'] and r['Level']=='重要' and not r['MigrationPending'] for r in migrated),'confirmed copying replaces target defaults, preserves importance and stays inactive')
            saved=(config/'continuous-rules.json').read_bytes();process.terminate();process.wait(timeout=6);process=start();c=Client(port);wait(ready,'idempotent restart');login();check((config/'continuous-rules.json').read_bytes()==saved,'restarting does not duplicate or reset migrated rules')
            process.terminate();process.wait(timeout=6);raw=json.loads((config/'decoder.json').read_text(encoding='utf-8-sig'))
            for item in raw['Packets']:item.pop('OuterSourceId',None);item.pop('TelemetryNamespace',None)
            save('decoder.json',raw);process=start();c=Client(port);wait(ready,'old decoder restart');login();check(not any(p['Enabled'] for p in decoder()['configuration']['Packets']) and not c.call('/api/catalog'),'legacy packets with no outer source are preserved but disabled until configured')
        finally:
            sender.close()
            if process and process.poll() is None:process.terminate();process.wait(timeout=6)
    (ROOT/'test-output/v0.9.1/namespace-report.json').write_text(json.dumps(dict(checks=len(checks),details=checks),ensure_ascii=False,indent=2),encoding='utf-8');print('All',len(checks),'namespace integration checks passed. Only loopback UDP was used.')

if __name__=='__main__':main()
