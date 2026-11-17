"""Classification migration and lossless/atomic command CSV import-export over HTTP."""
import csv,io,json,secrets,shutil,socket,subprocess,sys,tempfile,urllib.request
from pathlib import Path
from integration_test import ROOT,Client,free_port,wait,seed_send_monitor
HEADERS=['指令编号','指令名称','指令类别','来源IP','目的IP','完整UDP载荷HEX']
def csv_bytes(rows,encoding='utf-8-sig'):
    text=io.StringIO(newline='');writer=csv.writer(text);writer.writerow(HEADERS);writer.writerows(rows);return text.getvalue().encode(encoding)
def upload(client,raw,expected=200,csrf=True):
    request=urllib.request.Request(client.base+'/api/send-monitor/commands/preview',data=raw,headers={'Content-Type':'text/csv','X-CSRF-Token':client.csrf if csrf else ''})
    try:response=client.opener.open(request,timeout=20)
    except urllib.error.HTTPError as error:response=error
    body=response.read().decode('utf-8');assert response.status==expected,(response.status,body);return json.loads(body)
def main():
    exe=Path(sys.argv[1]).resolve() if len(sys.argv)>1 else ROOT/'TelemetryMonitor.exe';checks=[]
    def check(ok,label):assert ok,label;checks.append(label);print('PASS',label,flush=True)
    with tempfile.TemporaryDirectory(prefix='command-catalog-') as scratch:
        root=Path(scratch);shutil.copytree(ROOT/'config',root/'config');shutil.copytree(ROOT/'web',root/'web');seed_send_monitor(root)
        for name in ['decoder.json','continuous-rules.json','tc-bindings.json']:(root/'config'/name).unlink(missing_ok=True)
        path=root/'config/observed-commands.json';entries=[dict(Number=1,Name='原指令,"名称"\n换行',DestinationIP='192.168.1.113',DestinationIPs=['192.168.1.113','192.168.1.112','192.168.1.111'],PayloadHex='00 AA FF',Length=3,FirstSeenUtc='2026-11-02T00:00:00Z'),dict(Number=2,Name='KC原指令',DestinationIP='192.168.1.137',PayloadHex='01 02',Length=2,FirstSeenUtc='2026-11-02T00:00:00Z')]
        path.write_text(json.dumps(dict(SchemaVersion=1,SourceIP='127.0.0.1',IdentityRule='udp-payload',NextNumber=3,Commands=entries),ensure_ascii=False),encoding='utf-8');port=free_port();password=secrets.token_urlsafe(18)
        def start():return subprocess.Popen([str(exe),'--local','--root',str(root),'--web-port',str(port)],stdout=subprocess.DEVNULL,stderr=subprocess.STDOUT)
        process=start();c=Client(port)
        try:
            def ready():
                try:c.call('/api/auth/me');return True
                except OSError:return False
            def disk():return json.loads(path.read_text(encoding='utf-8-sig'))
            def listing(query=''):return c.call('/api/send-monitor/commands/list'+query)
            def apply(p,expected=200):return c.call('/api/send-monitor/commands/apply',dict(token=p['token']),expected=expected)
            def exported():
                raw=c.opener.open(c.base+'/api/send-monitor/commands/export.csv').read();return raw,list(csv.reader(io.StringIO(raw.decode('utf-8-sig'),newline='')))
            wait(ready,'command catalog');c.call('/api/send-monitor/commands/list',expected=401);c.call('/api/auth/setup',dict(username='catalogqa',name='Catalog QA',password=password));c.csrf=c.call('/api/auth/login',dict(username='catalogqa',password=password))['csrf']
            codes={r['code']:r for r in listing()['rows']};check(set(codes)=={'BC0001','PSS001','KC0001'} and codes['BC0001']['destinations']==['192.168.1.113','192.168.1.112'] and listing()['sourceCount']==2,'existing payloads classify by exact destinations, same BC destinations share one code and cross-category payload gets separate codes')
            check(listing('?kind=PSS')['matching']==1 and listing('?search=192.168.1.112')['rows'][0]['code']=='BC0001','category and destination searches return the correct row')
            raw,rows=exported();check(raw.startswith(b'\xef\xbb\xbf') and rows[0]==HEADERS and len(rows)==4 and rows[1][1]==entries[0]['Name'],'CSV uses six headers, UTF-8 BOM and preserves multiline quoted names')
            before=disk();p=upload(c,raw);check(p['canApply'] and p['addedCommands']==0 and p['addedCategories']==0 and p['addedDestinations']==0,'own CSV round trip previews no duplicate payload, destination or code');apply(p);check(disk()['NextCategoryNumbers']==before['NextCategoryNumbers'],'repeated import never consumes category numbers')
            new=[['','跨类别','BC','127.0.0.1','192.168.1.113','10 20'],['','跨类别','PSS','127.0.0.1','192.168.1.111','10 20'],['KC0010','KC显式编号','KC','127.0.0.1','192.168.1.137','FF'],['','其他IP','未分类','127.0.0.1','192.168.1.114','']]
            p=upload(c,csv_bytes(new));check(p['canApply'] and p['addedCommands']==3 and p['addedCategories']==3 and {r['code'] for r in p['rows']}=={'BC0002','PSS002','KC0010',''},'preview assigns independent codes, preserves explicit code and groups cross-category duplicate source')
            apply(p);check(listing()['sourceCount']==5 and listing()['total']==7 and disk()['NextCategoryNumbers']['KC']==11,'merge keeps prior commands and advances each category from its own maximum')
            p=upload(c,csv_bytes([['','KC下一个','KC','127.0.0.1','192.168.1.137','EE']]));check(p['rows'][0]['code']=='KC0011','next automatic KC code follows explicit imported maximum');apply(p)
            stable=path.read_bytes();bad=[['BC0001','冲突','BC','127.0.0.1','192.168.1.113','AB'],['PSS009','类别错','PSS','127.0.0.1','192.168.1.137','CD'],['','错来源','BC','127.0.0.2','192.168.1.112','EF']];p=upload(c,csv_bytes(bad));apply(p,400);check(not p['canApply'] and len(p['errors'])==3 and path.read_bytes()==stable,'code collision, mismatched kind and wrong source all report errors and cannot partially apply')
            p=upload(c,csv_bytes([['BC0009','不能改号','BC','127.0.0.1','192.168.1.113','00 AA FF']]));check(not p['canApply'] and '已有BC编号' in p['errors'][0],'existing category cannot be silently renumbered')
            p=upload(c,csv_bytes([['','改名','BC','127.0.0.1','192.168.1.112','10 20']],encoding='gbk'));check(p['canApply'] and p['renamed']==1,'Windows GBK CSV and intentional name updates are accepted');apply(p)
            raw,rows=exported();check(next(r for r in rows if r[0]=='PSS002')[1]=='改名','same payload retains one consistent name across categories')
            p=upload(c,csv_bytes([['','过期预览','BC','127.0.0.1','192.168.1.112','99']]));other=upload(c,csv_bytes([['','另一预览','BC','127.0.0.1','192.168.1.112','88']]));apply(p,400);apply(other)
            stale=upload(c,csv_bytes([['','下一条','PSS','127.0.0.1','192.168.1.111','77']]));settings=c.call('/api/config');settings.update(BindAddress='127.0.0.1',UdpPort=free_port(socket.SOCK_DGRAM),SourceIPs=['127.0.0.1'],MulticastGroup='',MulticastInterface='');c.call('/api/config',settings);c.call('/api/control',dict(action='start'));apply(stale,409);c.call('/api/control',dict(action='stop'));apply(stale)
            check(True,'import is blocked during reception and the prepared merge can be applied after stop')
            upload(c,csv_bytes(new),403,False);upload(c,b'wrong,headers\n',400);upload(c,('指令编号,指令名称,指令类别,来源IP,目的IP,完整UDP载荷HEX\n"broken').encode('utf-8'),400)
            check(True,'CSV import requires CSRF, valid headers and properly closed quotes')
            raw,_=exported();saved=disk();process.terminate();process.wait(timeout=6);process=start();c=Client(port);wait(ready,'catalog restart');c.csrf=c.call('/api/auth/login',dict(username='catalogqa',password=password))['csrf'];check(disk()==saved and exported()[0]==raw,'all codes, counters, destinations and CSV content persist unchanged across restart')
            tc=c.call('/api/continuous-rules/tc');number=disk()['Commands'][0]['Number'];c.call('/api/continuous-rules/tc',dict(number=number,code='LEGACY_CLEAR',revision=tc['revision']))
            clear=lambda:c.call('/api/send-monitor/commands/clear-preview')
            def erase(preview,expected=200,csrf=True):return c.call('/api/send-monitor/commands/clear',dict(confirm='clear-command-catalog',revision=str(preview['revision']),tcRevision=preview['tcRevision']),expected=expected,csrf=csrf)
            preview=clear();stable=disk();c.call('/api/send-monitor/commands/clear',dict(revision=str(preview['revision'])),expected=400);erase(preview,403,False);check(disk()==stable,'clearing requires explicit confirmation and CSRF')
            c.call('/api/control',dict(action='start'));erase(preview,409);c.call('/api/control',dict(action='stop'));check(disk()==stable,'live reception blocks command clearing')
            c.call('/api/continuous-rules/tc',dict(number=number,code='LEGACY_CHANGED',revision=c.call('/api/continuous-rules/tc')['revision']));erase(preview,409);check(disk()==stable,'a concurrent TC binding change invalidates clear confirmation')
            old_next=stable['NextNumber'];stale=upload(c,csv_bytes([['','obsolete preview','BC','127.0.0.1','192.168.1.113','66']]));erase(clear());apply(stale,400)
            empty=disk();check(listing()['total']==0 and empty['Commands']==[] and empty['SourceIP']=='127.0.0.1' and empty['NextNumber']==old_next and all(v==1 for v in empty['NextCategoryNumbers'].values()),'clearing removes every category, resets category counters and retains source/internal ID safety')
            check(c.call('/api/continuous-rules/tc')['bindings']==[] and len(exported()[1])==1,'manual bindings and obsolete previews are cleared; CSV contains only its header')
            process.terminate();process.wait(timeout=6);process=start();c=Client(port);wait(ready,'clear restart');c.csrf=c.call('/api/auth/login',dict(username='catalogqa',password=password))['csrf'];check(listing()['total']==0 and c.call('/api/continuous-rules/tc')['bindings']==[],'empty command library and bindings persist across restart')
            p=upload(c,csv_bytes([['','new command','BC','127.0.0.1','192.168.1.113','55']]));apply(p);check(disk()['Commands'][0]['Number']>=old_next and disk()['Commands'][0]['Categories'][0]['Code']=='BC0001','new commands restart category numbering without reusing old manual-binding IDs')
        finally:
            if process.poll() is None:process.terminate();process.wait(timeout=6)
    print('All',len(checks),'command catalog checks passed.')
if __name__=='__main__':main()
