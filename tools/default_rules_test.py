"""Default rule initialization through isolated authenticated import/edit APIs."""
import csv, io, json, secrets, shutil, socket, subprocess, tempfile, sys
from pathlib import Path
from integration_test import ROOT, Client, free_port, wait, seed_send_monitor, packet as udp_packet
from catalog_import_test import workbook
from decoder_integration_test import upload
from continuous_rules_test import workbook as rule_workbook, upload as upload_rules
TEMPLATE='InitRegion\n    this.state=0;\nEndInitRegion\n\nStateRegion\n\nEndStateRegion\n\nResultRegion\n    RET=Normal;\nEndResultRegion'

def main():
    exe = Path(sys.argv[1]).resolve() if len(sys.argv)>1 else ROOT/'TelemetryMonitor.exe'
    checks=[]
    def check(ok,label):
        assert ok,label
        checks.append(label);print('PASS',label,flush=True)
    with tempfile.TemporaryDirectory(prefix='default-rules-') as scratch:
        root=Path(scratch);shutil.copytree(ROOT/'config',root/'config');shutil.copytree(ROOT/'web',root/'web')
        for name in ['decoder.json','continuous-rules.json','tc-bindings.json']:(root/'config'/name).unlink(missing_ok=True)
        seed_send_monitor(root)
        cfg=json.loads((root/'config/server.json').read_text(encoding='utf-8-sig'));cfg['SourceIPs']=['127.0.0.1'];(root/'config/server.json').write_text(json.dumps(cfg),encoding='utf-8')
        port=free_port();password=secrets.token_urlsafe(18)
        def start():return subprocess.Popen([str(exe),'--local','--root',str(root),'--web-port',str(port)],stdout=subprocess.DEVNULL,stderr=subprocess.STDOUT)
        process=start()
        try:
            c=Client(port)
            def ready():
                try:c.call('/api/auth/me');return True
                except OSError:return False
            wait(ready,'isolated server');c.call('/api/auth/setup',dict(username='defaultqa',name='QA',password=password));c.csrf=c.call('/api/auth/login',dict(username='defaultqa',password=password))['csrf']
            def state():return c.call('/api/continuous-rules')
            def rows():return {x['rule']['Code']:x for x in state()['rows']}
            def apply(data,mode='replace'):
                p=upload(c,'default.xlsx',data);c.call('/api/decoders/apply',dict(token=p['token'],revision=p['revision'],mode=mode))
            a1=('A1','参数一',0,1,'无符号整数',1);a2=('A2','参数二',1,1,'无符号整数',1)
            data=workbook([('主表',[a1,a2])]);p=upload(c,'default.xlsx',data)
            check(state()['total']==0,'preview does not create rules')
            c.call('/api/decoders/apply',dict(token=p['token'],revision=p['revision'],mode='replace'))
            check(state()['total']==2 and all(r['rule']['Level']=='一般' and not r['rule']['Enabled'] and r['rule']['Script']==TEMPLATE and r['initialTemplate'] and not r['error'] and not r['canExecute'] and r['status']=='待绑定' for r in rows().values()),'every imported unassigned parameter gets the full three-region disabled default and neutral binding status')
            check(len(c.call('/api/continuous-rules/parameters'))==2,'unassigned parameters are available to the rule editor')
            current=c.call('/api/decoders');packet=current['configuration']['Packets'][0];packet.update(Groups=['主表'],Enabled=True,MappingConfirmed=True)
            original={k:v['rule']['Id'] for k,v in rows().items()}
            c.call('/api/decoders/packet',dict(revision=current['revision'],packet=packet))
            check(state()['total']==2 and all(r['rule']['Id']==original[k] and '::' in r['parameterId'] for k,r in rows().items()),'linking SPP migrates defaults without duplicate rows or changed rule identities')
            rule=dict(rows()['A2']['rule']);rule['Level']='重要';c.call('/api/continuous-rules/save',dict(rule=rule,revision=state()['revision']))
            check(rows()['A2']['rule']['Script']==TEMPLATE and rows()['A2']['rule']['Level']=='重要','default three-region rule can be edited and saved without losing its empty StateRegion')
            c.call('/api/continuous-rules/toggle',dict(id=rule['Id'],enabled=True,revision=state()['revision']))
            check(rows()['A2']['rule']['Enabled'],'a bound default template is executable and can be manually enabled')
            settings=c.call('/api/config');settings.update(UdpPort=free_port(socket.SOCK_DGRAM),BindAddress='127.0.0.1',MulticastGroup='',MulticastInterface='');c.call('/api/config',settings)
            c.call('/api/control',dict(action='start'));c.call('/api/continuous-rules/control',dict(action='start'))
            with socket.socket(socket.AF_INET,socket.SOCK_DGRAM) as sender:sender.sendto(udp_packet(1),('127.0.0.1',settings['UdpPort']))
            wait(lambda:rows()['A2']['result']=='Normal','default result');check(rows()['A2']['currentState']==0,'three-region default initializes this.state to zero and returns Normal only after explicit start and valid data')
            c.call('/api/continuous-rules/control',dict(action='stop'));c.call('/api/control',dict(action='stop'));c.call('/api/continuous-rules/toggle',dict(id=rule['Id'],enabled=False,revision=state()['revision']))
            p=upload_rules(c,rule_workbook([[1,'A2','参数二','一般','否','RANGE=[0 20];']]))
            c.call('/api/continuous-rules/apply',dict(token=p['token'],mode='add',enablePolicy='excel'),expected=400)
            check(p['conflicts'] and rows()['A2']['rule']['Level']=='重要' and rows()['A2']['rule']['Script']==TEMPLATE,'rule addition protects an edited initial configuration')
            rule=dict(rows()['A1']['rule']);rule.update(Level='关键',Enabled=True,Script='RANGE=[0 10];')
            c.call('/api/continuous-rules/save',dict(rule=rule,revision=state()['revision']))
            configured=dict(rows()['A1']['rule'])
            a3=('A3','新参数',2,1,'无符号整数',1)
            apply(workbook([('主表',[a1,a3])]),'add')
            check(state()['total']==3 and rows()['A1']['rule']==configured and rows()['A2']['rule']['Level']=='重要' and rows()['A3']['initialTemplate'],'addition initializes only missing parameters and preserves all existing rule settings')
            exported=list(csv.reader(io.StringIO(c.opener.open(c.base+'/api/continuous-rules/export.csv').read().decode('utf-8-sig'),newline='')))
            check(next(r for r in exported[1:] if r[1]=='A3')[5]==TEMPLATE,'six-column CSV preserves every region including the empty StateRegion')
            p=upload_rules(c,rule_workbook([[1,'A3','新参数','重要','是','RANGE=[0 20];']]))
            check(p['accepted']==1 and not p['conflicts'],'rule Excel addition can fill an untouched initialized parameter')
            c.call('/api/continuous-rules/apply',dict(token=p['token'],mode='add',enablePolicy='excel'))
            check(state()['total']==3 and rows()['A3']['rule']['Script']=='RANGE=[0 20];' and rows()['A3']['rule']['Enabled'],'rule import replaces default placeholder despite unchanged row count and resolved source IP')
            p=upload_rules(c,rule_workbook([[1,'A2','参数二','一般','否','']]))
            check(p['accepted']==0 and p['rejected']==1,'empty rule Excel row remains rejected as nonexecutable')
            apply(workbook([('主表',[a1,a3])]))
            check(state()['total']==2 and rows()['A1']['rule']==configured,'decoder replacement removes absent blank defaults and preserves edited executable rules')
            stable=c.call('/api/continuous-rules/export');apply(workbook([('主表',[a1,a3])]))
            check(c.call('/api/continuous-rules/export')==stable,'repeated decoder import is idempotent for existing judgment configuration')
            # Old installations gain defaults once, then retain them across restarts.
            process.terminate();process.wait(timeout=6)
            saved=root/'config/continuous-rules.json';existing=json.loads(saved.read_text(encoding='utf-8-sig'));existing['Rules']=existing['Rules'][:1];saved.write_text(json.dumps(existing),encoding='utf-8')
            process=start();c=Client(port);wait(ready,'restart');c.csrf=c.call('/api/auth/login',dict(username='defaultqa',password=password))['csrf']
            check(state()['total']==2 and sum(bool(r.get('initialTemplate')) for r in state()['rows'])==1,'upgrade startup backfills existing decoder parameters without overwriting existing rules')
            stable=c.call('/api/continuous-rules/export');process.terminate();process.wait(timeout=6);process=start();c=Client(port);wait(ready,'second restart');c.csrf=c.call('/api/auth/login',dict(username='defaultqa',password=password))['csrf']
            check(c.call('/api/continuous-rules/export')==stable,'default state is persisted and restart is idempotent')
            process.terminate();process.wait(timeout=6);old=json.loads(saved.read_text(encoding='utf-8-sig'));legacy=next(r for r in old['Rules'] if r['Code']=='A3');legacy['Script']='';legacy['Level']='重要';legacy_id=legacy['Id'];saved.write_text(json.dumps(old),encoding='utf-8')
            process=start();c=Client(port);wait(ready,'legacy blank upgrade');c.csrf=c.call('/api/auth/login',dict(username='defaultqa',password=password))['csrf']
            check(rows()['A3']['rule']['Id']==legacy_id and rows()['A3']['rule']['Script']==TEMPLATE and rows()['A3']['rule']['Level']=='重要' and not rows()['A3']['rule']['Enabled'] and rows()['A1']['rule']==configured,'legacy blank defaults migrate to three regions while identity, importance, disabled flag and custom peer remain intact')
        finally:
            if process.poll() is None:process.terminate();process.wait(timeout=6)
    print('All',len(checks),'default-rule checks passed.')

if __name__=='__main__':main()
