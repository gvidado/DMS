"""Six-column rule format, HTTP management and real loopback continuous judgments."""
import csv,hashlib,io,json,secrets,shutil,socket,struct,subprocess,tempfile,zipfile,urllib.request
from pathlib import Path
from xml.sax.saxutils import escape
from integration_test import ROOT,Client,free_port,packet,seal,seed_send_monitor,wait

HEADERS=['序号','参数代号','参数名称','判读级别','是否判读','判读规则']
RANGE='InitRegion\nthis.state=0;\nEndInitRegion\nResultRegion\nif(TM.A4.value<0) then RANGE=[0 20]; else RET=SeriousError; end\nEndResultRegion'
ENUM='if(INENUM(this.value)) then RET=Normal; else RET=Error; end'

def workbook(rows,headers=HEADERS):
    ns='http://schemas.openxmlformats.org/spreadsheetml/2006/main'
    def line(n,values):return '<row r="%d">%s</row>'%(n,''.join('<c r="%s%d" t="inlineStr"><is><t xml:space="preserve">%s</t></is></c>'%(chr(65+i),n,escape(str(v))) for i,v in enumerate(values)))
    out=io.BytesIO()
    with zipfile.ZipFile(out,'w',zipfile.ZIP_DEFLATED) as z:
        z.writestr('[Content_Types].xml','<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"/>')
        z.writestr('xl/workbook.xml','<workbook xmlns="%s" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets><sheet name="判读规则" sheetId="1" r:id="s1"/></sheets></workbook>'%ns)
        z.writestr('xl/_rels/workbook.xml.rels','<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="s1" Target="worksheets/sheet1.xml"/></Relationships>')
        z.writestr('xl/worksheets/sheet1.xml','<worksheet xmlns="%s"><sheetData>%s</sheetData></worksheet>'%(ns,line(1,headers)+''.join(line(i+2,r) for i,r in enumerate(rows))))
    return out.getvalue()

def upload(client,data,name='rules.xlsx',expected=200,csrf=True):
    req=urllib.request.Request(client.base+'/api/continuous-rules/preview?filename='+name,data=data,headers={'Content-Type':'application/octet-stream','X-CSRF-Token':client.csrf if csrf else ''})
    try:r=client.opener.open(req,timeout=15)
    except urllib.error.HTTPError as error:r=error
    payload=r.read().decode('utf-8');assert r.status==expected,(r.status,payload);return json.loads(payload)

def main():
    checks=[]
    def check(ok,label):assert ok,label;checks.append(label);print('PASS',label,flush=True)
    with tempfile.TemporaryDirectory(prefix='continuous-rules-') as scratch:
        directory=Path(scratch);shutil.copytree(ROOT/'config',directory/'config');shutil.copytree(ROOT/'web',directory/'web');(directory/'config/decoder.json').unlink(missing_ok=True);(directory/'config/continuous-rules.json').unlink(missing_ok=True);seed_send_monitor(directory)
        settings=json.loads((directory/'config/server.json').read_text(encoding='utf-8-sig'));settings.update(BindAddress='127.0.0.1',SourceIPs=['127.0.0.1'],UdpPort=free_port(socket.SOCK_DGRAM),MappingConfirmed=True,DataTimeoutMs=4000);(directory/'config/server.json').write_text(json.dumps(settings),encoding='utf-8')
        port=free_port();process=subprocess.Popen([str(ROOT/'TelemetryMonitor.exe'),'--local','--root',str(directory),'--web-port',str(port)],stdout=subprocess.PIPE,stderr=subprocess.STDOUT)
        sender=socket.socket(socket.AF_INET,socket.SOCK_DGRAM);sender.bind(('127.0.0.1',0));seq=0
        try:
            c=Client(port)
            def ready():
                try:return c.call('/api/auth/me')['setupRequired']
                except OSError:return False
            wait(ready,'rules server');password=secrets.token_urlsafe(18);c.call('/api/auth/setup',dict(username='rule_qa',name='Rule QA',password=password));c.csrf=c.call('/api/auth/login',dict(username='rule_qa',password=password))['csrf']
            def state():return c.call('/api/continuous-rules')
            def rows():return {r['rule']['Code'].upper():r for r in state()['rows']}
            def apply(preview,mode='replace',policy='excel',expected=200):return c.call('/api/continuous-rules/apply',dict(token=preview['token'],mode=mode,enablePolicy=policy),expected=expected)
            def control(action):return c.call('/api/continuous-rules/control',dict(action=action))
            def toggle(code,enabled):
                running=state()['execution']['running'];control('stop');r=rows()[code]['rule'];result=c.call('/api/continuous-rules/toggle',dict(id=r['Id'],enabled=enabled,revision=state()['revision']))
                if running:control('start')
                return result
            def save(code,script,enabled=True,level=None,expected=200):
                running=state()['execution']['running'];control('stop');r=dict(rows()[code]['rule']);r.update(Script=script,Enabled=enabled)
                if level:r['Level']=level
                result=c.call('/api/continuous-rules/save',dict(rule=r,revision=state()['revision']),expected=expected)
                if running:control('start')
                return result
            def send(value=123456,enum=0x020201,float_value=-2.5):
                nonlocal seq
                seq+=1;data=packet(seq);data[22:27]=value.to_bytes(5,'big');data[27:30]=enum.to_bytes(3,'big');struct.pack_into('>f',data,35,float_value);seal(data);sender.sendto(data,('127.0.0.1',settings['UdpPort']));wait(lambda:c.call('/api/state')['accepted']>=seq,'rule UDP')
            def judged(code,result):wait(lambda:rows()[code]['result']==result,code+' '+result)
            def alarms():return [r for r in c.call('/api/alarms/list?pageSize=100')['records'] if r['category']=='常态化判读']
            valid=[[1,'a1','Time rule','关键','是',RANGE],[2,'A2','Mode rule','重要','是',ENUM],[3,'A4','Float rule','一般','否','EV=-2.5,ERR=0;']]
            fixture=workbook(valid+[[4,'MISSING','missing','一般','是','RANGE=[0 1];'],[5,'A5','empty','一般','否',''],[6,'A6','invalid','一般','是','RANGE=[10 0];'],[7,'A7','unknown','一般','是','AFTER(TC.K001,4s)']])
            Client(port).call('/api/continuous-rules',expected=401);upload(c,fixture,expected=403,csrf=False)
            check(state()['total']==0 and not (directory/'config/continuous-rules.json').exists(),'rules start empty and require authentication and CSRF')
            preview=upload(c,fixture)
            check(preview['accepted']==3 and preview['rejected']==4 and state()['total']==0,'six-column preview accepts only executable rules and reports every rejected row without saving')
            check(preview['rules'][0]['ParameterId']=='工仪全程/A1' and preview['rules'][0]['Script']==RANGE,'unique case-insensitive row binding preserves source rule text and canonical parameter identity')
            errors=c.opener.open(c.base+'/api/continuous-rules/errors?token='+preview['token']).read().decode('utf-8-sig')
            check(len(list(csv.reader(io.StringIO(errors))))==5,'all import errors export as CSV')
            apply(preview,policy='disabled');check(state()['enabled']==0 and len(rows())==3,'bulk import can override Excel flags to keep all imported rules disabled')
            toggle('A1',True);toggle('A2',True);c.call('/api/selection',dict(ids=[]));c.call('/api/control',dict(action='start'));control('start');send();judged('A1','Normal');judged('A2','Normal')
            check(rows()['A4']['status']=='已停用' and rows()['A1']['rule']['Level']=='关键','parallel judgments use numeric engineering data independently of visible selection, importance and disabled peers')
            send(value=300000,enum=0xffffff);judged('A1','Error');judged('A2','Error');check(len(alarms())==2,'range and INENUM failures each create an actual alarm')
            send(value=300000,enum=0xffffff);check(len(alarms())==2,'persistent identical failures do not emit an alarm on every frame')
            c.call('/api/control',dict(action='ack'));send(value=300000,enum=0xffffff);check(len(alarms())==2 and all(r['confirmed'] for r in alarms()),'acknowledgment keeps evidence without recurring alarms for the same condition')
            send();judged('A1','Normal');send(value=300000,enum=0xffffff);judged('A1','Error');check(len(alarms())==4,'normal-to-error recurrence creates new records')
            toggle('A1',False);save('A2',ENUM,level='关键');send();judged('A2','Normal');check(rows()['A1']['status']=='已停用' and rows()['A2']['rule']['Level']=='关键','web-style edits and enable changes apply during reception without stopping other rules')
            save('A4','VAL=this.value-this.pre1; RANGE=[-0.1 0.1];');send();judged('A4','Normal');send(float_value=-2);judged('A4','Error');check(True,'historical engineering values participate in live calculations')
            save('A4','InitRegion this.state=0; EndInitRegion StateRegion if(this.value>0) then this.state=1; else this.state=0; end EndStateRegion ResultRegion if(this.state==1) then EV=2,ERR=0.2; else EV=-2.5; end EndResultRegion');send(float_value=2);judged('A4','Normal');check(True,'three-region state and result evaluation uses persistent per-rule state')
            save('A4','RANGE=[-3 [-2 [-1 1] 2] 3];')
            for v,result in [(1,'Normal'),(1.5,'Unusual'),(2.5,'Error'),(3.5,'SeriousError')]:send(float_value=v);judged('A4',result)
            check(True,'nested ranges preserve closed boundaries and four result levels')
            save('A4','RANGE=[10 0];',expected=400);save('A4','if(INENUM(this.value)) then RET=Normal; end',expected=400)
            check(rows()['A4']['rule']['Script'].startswith('RANGE=[-3'),'invalid edits and missing annotation definitions leave the saved rule intact')
            control('stop')
            # New manual rule is independent of Excel provenance.
            c.call('/api/continuous-rules/save',dict(revision=state()['revision'],rule=dict(Code='A8',Name='Manual rule',Level='重要',Enabled=False,Script='EL=0,EU=1;',ParameterId='工仪全程/A8')))
            check('A8' in rows(),'manual creation saves a new parameter rule')
            old=rows()['A1']['rule'];same=[1,old['Code'],old['Name'],old['Level'],'是' if old['Enabled'] else '否',old['Script']]
            addition=upload(c,workbook([same,[2,'A3','New rule','一般','否','RANGE=[0 255];']]))
            check(addition['duplicates']==1 and not addition['conflicts'],'addition previews skip identical existing definitions')
            apply(addition,'add');check(len(rows())==5 and rows()['A8']['rule']['Name']=='Manual rule','addition preserves manually created rules and adds only new parameters')
            stable=(directory/'config/continuous-rules.json').read_bytes();conflict=upload(c,workbook([[1,'A1','Changed','一般','否','RANGE=[0 9];'],[2,'A5','Would add','一般','否','RANGE=[0 1];']]))
            apply(conflict,'add',expected=400);check((directory/'config/continuous-rules.json').read_bytes()==stable and 'A5' not in rows(),'conflicting addition is atomic and never overwrites or partially appends')
            stale=upload(c,workbook([[1,'A6','Stale','一般','否','RANGE=[0 1];']]))
            toggle('A1',True);apply(stale,'add',expected=409);check('A6' not in rows(),'stale import cannot overwrite another saved revision')
            all_bad=upload(c,workbook([[1,'A9','Empty','一般','否','']]));apply(all_bad,expected=400);check(len(rows())==5,'all-invalid replacement never erases existing rules')
            duplicates=upload(c,workbook([[1,'A9','one','一般','否','RANGE=[0 1];'],[2,'a9','two','一般','否','RANGE=[0 2];']]))
            check(duplicates['accepted']==0 and duplicates['rejected']==2,'duplicate parameter bindings in one workbook reject all conflicting rows')
            c.call('/api/control',dict(action='stop'))
            replacement=upload(c,workbook([[1,'A4','Replaced','重要','否','RANGE=[-5 5];']]))
            apply(replacement);check(state()['total']==1 and rows()['A4']['rule']['Name']=='Replaced','replacement updates only rule configuration and removes prior rules')
            exported=c.call('/api/continuous-rules/export');process.terminate();process.wait(timeout=6)
            process=subprocess.Popen([str(ROOT/'TelemetryMonitor.exe'),'--local','--root',str(directory),'--web-port',str(port)],stdout=subprocess.PIPE,stderr=subprocess.STDOUT);c=Client(port)
            def restarted():
                try:return not c.call('/api/auth/me')['setupRequired']
                except OSError:return False
            wait(restarted,'rule restart');c.csrf=c.call('/api/auth/login',dict(username='rule_qa',password=password))['csrf']
            check(c.call('/api/continuous-rules/export')==exported,'levels, enable state and scripts persist across server restart')
            check(not (directory/'config/decoder.json').exists(),'rule operations never create or replace telemetry decoder configuration')
        finally:
            sender.close()
            if process.poll() is None:process.terminate();process.wait(timeout=6)
    # Generic fixture follows only the requested six-column format, never the user's sample contents.
    path=ROOT/'test-output/rules-format-fixture.xlsx';path.parent.mkdir(exist_ok=True);path.write_bytes(workbook([[1,'A1','Time rule','关键','是','RANGE=[0 20];'],[2,'A2','Mode rule','重要','否',ENUM],[3,'NOT_FOUND','Missing','一般','否','RANGE=[0 1];']]))
    (ROOT/'test-output/rules-add-fixture.xlsx').write_bytes(workbook([[1,'A3','Added rule','一般','否','RANGE=[0 255];']]))
    print('All',len(checks),'continuous-rule checks passed.')

if __name__=='__main__':main()
