"""PSS padding, legacy migration, CSV, editing and sequence references; no UDP sends."""
import csv,io,json,secrets,shutil,subprocess,sys,tempfile,urllib.request,urllib.parse
from pathlib import Path
from integration_test import ROOT,Client,free_port,wait,seed_send_monitor
from sequence_test import xlsx

def main():
    exe=Path(sys.argv[1]).resolve();checks=[]
    def check(ok,label):
        assert ok,label
        checks.append(label);print('PASS',label,flush=True)
    with tempfile.TemporaryDirectory(prefix='pss-numbering-',dir=ROOT/'test-output') as scratch:
        root=Path(scratch);config=root/'config';shutil.copytree(ROOT/'config',config);shutil.copytree(ROOT/'web',root/'web');seed_send_monitor(root)
        for name in ['decoder.json','continuous-rules.json','tc-bindings.json','sequences.json','observed-commands.json']:(config/name).unlink(missing_ok=True)
        def save(name,value):(config/name).write_text(json.dumps(value,ensure_ascii=False),encoding='utf-8')
        def load(name):return json.loads((config/name).read_text(encoding='utf-8-sig'))
        entries=[dict(Number=1,Name='原PSS92',DestinationIP='192.168.1.111',DestinationIPs=['192.168.1.111'],PayloadHex='11 AA',Length=2,FirstSeenUtc='2026-11-05T00:00:00Z',Categories=[dict(Kind='PSS',Code='PSS0092',DestinationIPs=['192.168.1.111'])]),dict(Number=2,Name='跨类PSS1',DestinationIP='192.168.1.113',DestinationIPs=['192.168.1.113','192.168.1.111'],PayloadHex='22 BB',Length=2,FirstSeenUtc='2026-11-05T00:00:00Z',Categories=[dict(Kind='BC',Code='BC0001',DestinationIPs=['192.168.1.113']),dict(Kind='PSS',Code='PSS0001',DestinationIPs=['192.168.1.111'])])]
        save('observed-commands.json',dict(SchemaVersion=1,SourceIP='127.0.0.1',IdentityRule='udp-payload',NextNumber=3,NextCategoryNumbers={'BC':2,'PSS':93,'KC':1},Commands=entries))
        script='// TC.PSS0092 comment unchanged\nResultRegion\nif(TC.PSS0092) then RET=Normal; else RET=Error; end\nEndResultRegion'
        save('continuous-rules.json',dict(SchemaVersion=1,Revision='before-rules',Rules=[dict(Id='pss-rule',Code='A1',Name='PSS规则',Level='一般',Script=script,ParameterId='工仪全程/A1',Enabled=False,Bindings={})]))
        save('sequences.json',dict(SchemaVersion=1,Revision='before-sequence',DefaultTimeoutSeconds=30,Sequences=[dict(Id='1'*32,Code='XLP0001',Name='PSS引用序列',Mode='judge',SourceIP='127.0.0.1',Rows=[dict(Row=n,Keyword='',Description='PSS0092 retained description',Command=code,Condition='',Mark='',Expected='',Timeout='') for n,code in enumerate(['PSS0092','TC.PSS0092','PSS0001'],2)])]))
        save('tc-bindings.json',dict(SchemaVersion=1,Revision='before-tc',Bindings=[dict(Code='KPS',Number=1)]))
        (root/'data').mkdir();sentinel=root/'data/history-sentinel.txt';sentinel.write_text('Historical PSS0092 stays unchanged',encoding='utf-8');tc_before=(config/'tc-bindings.json').read_bytes()
        port=free_port();password=secrets.token_urlsafe(18);process=None;c=Client(port)
        def start():return subprocess.Popen([str(exe),'--local','--root',str(root),'--web-port',str(port)],stdout=subprocess.DEVNULL,stderr=subprocess.STDOUT,creationflags=subprocess.CREATE_NO_WINDOW)
        def ready():
            try:c.call('/api/auth/me');return True
            except OSError:return False
        def listing():return c.call('/api/send-monitor/commands/list')
        def upload(data,path='/api/send-monitor/commands/preview'):
            req=urllib.request.Request(c.base+path,data=data,headers={'Content-Type':'application/octet-stream','X-CSRF-Token':c.csrf});return json.loads(c.opener.open(req,timeout=15).read())
        def rows_bytes(rows):
            out=io.StringIO();writer=csv.writer(out);writer.writerow(['指令编号','指令名称','指令类别','来源IP','目的IP','完整UDP载荷HEX']);writer.writerows(rows);return out.getvalue().encode('utf-8-sig')
        try:
            process=start();wait(ready,'PSS server startup');c.call('/api/auth/setup',dict(username='psstest',name='PSS测试',password=password));c.csrf=c.call('/api/auth/login',dict(username='psstest',password=password))['csrf']
            codes={r['code']:r for r in listing()['rows']}
            check(set(codes)=={'PSS092','PSS001','BC0001'},'legacy PSS0092/PSS0001 become PSS092/PSS001; BC remains four digits')
            after=load('observed-commands.json');check([(e['Number'],e['Name'],e['PayloadHex'],e['DestinationIPs']) for e in after['Commands']]==[(e['Number'],e['Name'],e['PayloadHex'],e['DestinationIPs']) for e in entries],'migration preserves IDs, names, UDP payloads and destinations')
            rules=load('continuous-rules.json')['Rules'];rule=next(r for r in rules if r['Id']=='pss-rule');check('if(TC.PSS092)' in rule['Script'] and '// TC.PSS0092 comment unchanged' in rule['Script'],'saved TC references migrate without changing comments or identifier boundaries')
            sequence=load('sequences.json')['Sequences'][0];check([r['Command'] for r in sequence['Rows']]==['PSS092','TC.PSS092','PSS001'] and sequence['Rows'][0]['Description']=='PSS0092 retained description','saved sequence command references migrate while descriptions stay unchanged')
            check((config/'tc-bindings.json').read_bytes()==tc_before and sentinel.read_text(encoding='utf-8')=='Historical PSS0092 stays unchanged','manual aliases and historical files remain unchanged')
            conversion=upload(xlsx([('转换',[['TEXT','发送 SETP.PSS092','SETP.PSS092','','','','']])]),'/api/sequence/preview?filename=pss.xlsx&mode=judge&sourceIP=127.0.0.1&timeout=30&convert=commands')
            row=conversion['definitions'][0]['definition']['Rows'][0];check(row['Command']=='PSS092' and row['Keyword']=='','sequence tool matches three-digit PSS within legacy prefixes')
            legacy=upload(rows_bytes([['PSS0007','CSV旧格式','PSS','127.0.0.1','192.168.1.111','33 CC']]))
            check(legacy['canApply'] and legacy['rows'][0]['code']=='PSS007','legacy four-digit CSV is previewed as the canonical three-digit code');c.call('/api/send-monitor/commands/apply',dict(token=legacy['token']))
            automatic=upload(rows_bytes([['','下一条PSS','PSS','127.0.0.1','192.168.1.111','44 DD']]))
            check(automatic['canApply'] and automatic['rows'][0]['code']=='PSS093','automatic allocation preserves next numeric index with three-digit padding');c.call('/api/send-monitor/commands/apply',dict(token=automatic['token']))
            entry=next(r for r in listing()['rows'] if r['code']=='PSS092');result=c.call('/api/send-monitor/commands/edit',dict(number=entry['number'],originalCode='PSS092',code='PSS094',name=entry['name'],revision=listing()['revision']))
            check(result['renamedRules']==1 and result['renamedSequenceRows']==2 and 'TC.PSS094' in next(r for r in load('continuous-rules.json')['Rules'] if r['Id']=='pss-rule')['Script'],'editing accepts PSS094 and atomically updates saved rules and sequence references')
            for invalid in ['PSS94','PSS0094','PSS000','PSS007']:
                response=c.call('/api/send-monitor/commands/edit',dict(number=entry['number'],originalCode='PSS094',code=invalid,name=entry['name'],revision=listing()['revision']),expected=400);check(bool(response.get('error')),'reject invalid or duplicate PSS edit '+invalid)
            req=urllib.request.Request(c.base+'/api/send-monitor/commands/export.csv');exported=c.opener.open(req).read();export_rows=list(csv.reader(io.StringIO(exported.decode('utf-8-sig'))));check({r[0] for r in export_rows[1:] if r[2]=='PSS'}=={'PSS001','PSS007','PSS093','PSS094'},'CSV export contains only current canonical PSS identifiers')
            roundtrip=upload(exported);check(roundtrip['canApply'] and not roundtrip['addedCommands'] and not roundtrip['addedCategories'],'exported CSV reimports without duplicate commands')
            large=upload(rows_bytes([['PSS999','末尾','PSS','127.0.0.1','192.168.1.111','55 EE']]))
            c.call('/api/send-monitor/commands/apply',dict(token=large['token']));next_item=upload(rows_bytes([['','继续','PSS','127.0.0.1','192.168.1.111','66 FF']]))
            check(next_item['canApply'] and next_item['rows'][0]['code']=='PSS1000','numeric overflow expands naturally rather than truncating or reusing a code')
            stable={n:(config/n).read_bytes() for n in ['observed-commands.json','continuous-rules.json','sequences.json','tc-bindings.json']};process.terminate();process.wait(timeout=6);process=start();c=Client(port);wait(ready,'PSS restart')
            check(all((config/n).read_bytes()==b for n,b in stable.items()),'restarting a migrated library is idempotent')
        finally:
            if process and process.poll() is None:process.terminate();process.wait(timeout=6)
    (ROOT/'test-output/v0.8.28/pss-numbering-report.json').write_text(json.dumps(dict(checks=len(checks),details=checks),ensure_ascii=False,indent=2),encoding='utf-8');print('All',len(checks),'PSS numbering checks passed. No device traffic sent.')

if __name__=='__main__':main()
