"""Daily compact judgments and named per-run archives on an isolated loopback server."""
import csv,io,json,secrets,shutil,socket,struct,subprocess,sys,tempfile,time,urllib.request
from datetime import datetime
from pathlib import Path
from urllib.parse import urlencode
from integration_test import ROOT,Client,free_port,packet,seal,seed_send_monitor,wait
from sequence_test import xlsx


def main():
    exe=Path(sys.argv[1]).resolve();checks=[]
    def check(ok,label):assert ok,label;checks.append(label);print('PASS',label,flush=True)
    with tempfile.TemporaryDirectory(prefix='judgment-archives-',dir=ROOT/'test-output') as temp:
        root=Path(temp);shutil.copytree(ROOT/'config',root/'config');shutil.copytree(ROOT/'web',root/'web');seed_send_monitor(root)
        for name in ['decoder.json','continuous-rules.json','tc-bindings.json','sequences.json']:(root/'config'/name).unlink(missing_ok=True)
        data=root/'data';data.mkdir();old=dict(Kind='abnormal',Id=1,Time='2026-10-01 10:00:00.000',Code='OLD',Name='历史参数',ValueText='-2',EngineeringValue='-2',Result='Error',Script='RET=Error;',RuleId='old',ParameterId='old/OLD',SourceIP='127.0.0.1')
        (data/'judgment-abnormal-history.jsonl').write_text(json.dumps(old,ensure_ascii=False)+'\n',encoding='utf-8');legacy_run='a'*32
        legacy=[dict(Event=event,RunId=legacy_run,Mode='judge',Sequence='历史序列',Sheet='旧表',Operator='原操作人',Time='2026-10-02 10:00:00.000',Status='全部通过' if event=='结束' else '运行中') for event in ['开始','结束']]
        (data/'sequence-history.jsonl').write_bytes(('\n'.join(json.dumps(e,ensure_ascii=False) for e in legacy)+'\n').encode('utf-8'))
        port=free_port();udp=free_port(socket.SOCK_DGRAM);settings=json.loads((root/'config/server.json').read_text('utf-8-sig'));settings.update(BindAddress='127.0.0.1',UdpPort=udp,SourceIPs=['127.0.0.1'],MappingConfirmed=True,DataTimeoutMs=60000,MulticastGroup='',MulticastInterface='');(root/'config/server.json').write_text(json.dumps(settings),encoding='utf-8')
        c=Client(port);password=secrets.token_urlsafe(18);sender=socket.socket(socket.AF_INET,socket.SOCK_DGRAM);sender.bind(('127.0.0.1',0));seq=0;today=datetime.now().strftime('%Y-%m-%d');daily_id='continuous:'+today
        def launch():return subprocess.Popen([str(exe),'--local','--root',str(root),'--web-port',str(port)],stdout=subprocess.DEVNULL,stderr=subprocess.STDOUT)
        def ready():
            try:c.call('/api/auth/me');return True
            except OSError:return False
        def listing(**filters):return c.call('/api/judgment-archives?'+urlencode(filters))
        def detail(id):return c.call('/api/judgment-archives/detail?'+urlencode(dict(id=id)))
        def send(value):
            nonlocal seq
            seq+=1;before=c.call('/api/state')['accepted'];raw=packet(seq);struct.pack_into('>f',raw,35,value);sender.sendto(seal(raw),('127.0.0.1',udp));wait(lambda:c.call('/api/state')['accepted']>before,'archive UDP frame')
        def control(action):return c.call('/api/continuous-rules/control',dict(action=action))
        process=launch()
        try:
            wait(ready,'archive server');c.call('/api/judgment-archives',expected=401);c.call('/api/judgment-archives/export.csv?id=x',expected=401);c.call('/api/auth/setup',dict(username='archiveqa',name='存档测试',password=password));c.csrf=c.call('/api/auth/login',dict(username='archiveqa',password=password))['csrf']
            check(listing()['history']['total']==2 and (data/'archives/continuous/2026-10-01.json').exists() and (data/'archives/sequences/2026-10-02'/f'{legacy_run}.jsonl').exists(),'historical abnormal incidents and old sequence runs are backfilled by their original dates')
            check(listing(name='历史参数')['history']['total']==1 and listing(name='历史序列')['history']['total']==1 and listing(**{'from':'2026-10-01','to':'2026-10-01'})['history']['total']==1,'archives are searchable by parameter/sequence name and date range')
            for query in ['from=wrong','from=2026-10-03&to=2026-10-01','type=wrong','page=0']:c.call('/api/judgment-archives?'+query,expected=400)
            c.call('/api/judgment-archives/detail?id=../../config/accounts.json',expected=400);check(True,'archive access requires login and rejects invalid filters and paths')
            rules=c.call('/api/continuous-rules');catalog=c.call('/api/catalog')
            for code,enabled,script in [('A1',True,'RET=Normal;'),('A4',True,'RANGE=[0 1];'),('A2',False,'RET=Error;')]:
                parameter=next(p for p in catalog if p['Code']==code);rule=dict(Code=code,Name='存档'+code,Level='重要',Enabled=enabled,ParameterId=parameter['Id'],SourceIP='127.0.0.1',Script=script);rules=c.call('/api/continuous-rules');c.call('/api/continuous-rules/save',dict(revision=rules['revision'],rule=rule))
            adapter=next(i for i in c.call('/api/send-monitor/interfaces')['interfaces'] if i['loopback']);c.call('/api/send-monitor/config',dict(SourceIPs=['127.0.0.1'],DestinationIPs=['127.0.0.2'],InterfaceId=adapter['id'],DestinationPort=0));c.call('/api/control',dict(action='start'));control('start')
            check(listing(**{'from':today,'to':today})['history']['total']==0,'waiting for actual data never creates a false daily pass')
            send(.5);current=detail(daily_id);check(len(current['records'])==2 and all(r['result']=='Normal' for r in current['records']) and {r['code'] for r in current['records']}=={'A1','A4'},'only enabled evaluated variables get one daily pass each')
            path=data/'archives/continuous'/f'{today}.json';before=path.read_bytes();send(.6);send(.7);check(path.read_bytes()==before,'additional passing frames do not append or rewrite daily summary')
            send(2);send(3);current=detail(daily_id);a4=[r for r in current['records'] if r['code']=='A4'];check(len(a4)==1 and a4[0]['kind']=='abnormal' and current['summary']['normal']==1 and current['summary']['abnormal']==1,'the first abnormal removes that variable pass; sustained abnormality remains one episode')
            send(.5);send(-2);current=detail(daily_id);a4=[r for r in current['records'] if r['code']=='A4'];check(len(a4)==2 and all(r['kind']=='abnormal' for r in a4),'recovery followed by a new abnormal records another occurrence without a misleading daily pass')
            incident=a4[0]['incidentId'];c.call('/api/abnormal-records/release',dict(key='judge:'+str(incident),reason='已核实存档异常'));check(next(r for r in detail(daily_id)['records'] if r['incidentId']==incident)['reason']=='已核实存档异常','archived abnormal detail reflects its manual release reason')
            control('stop');c.call('/api/control',dict(action='stop'));c.call('/api/control',dict(action='start'));control('start');send(.5);check(len(detail(daily_id)['records'])==3 and len(list((data/'archives/continuous').glob(today+'.json')))==1,'multiple listener runs on one day share the same file and preserve all existing abnormal episodes')
            raw=c.opener.open(c.base+'/api/judgment-archives/export.csv?'+urlencode(dict(id=daily_id))).read();rows=list(csv.DictReader(io.StringIO(raw.decode('utf-8-sig'))));check(raw.startswith(b'\xef\xbb\xbf') and len(rows)==3 and any(r['解除原因']=='已核实存档异常' for r in rows) and any(r['异常值']=='-2' for r in rows),'daily CSV exports compact results, exact negative values and manual release metadata')
            control('stop')
            def install(condition='TM.A4.value==0.5',mode='judge'):
                # Configuration saves during listening are staged for two minutes.
                # Keep this archive test independent of that scheduling policy.
                was_live=c.call('/api/state')['mode']=='live'
                if was_live:c.call('/api/control',dict(action='stop'))
                request=urllib.request.Request(c.base+'/api/sequence/preview?'+urlencode(dict(filename='archive.xlsx',mode=mode,timeout=.15,sourceIP='127.0.0.1')),data=xlsx([('原始工作表',[['TEXT','归档说明'],['','参数步骤','',condition]])]),headers={'Content-Type':'application/octet-stream','X-CSRF-Token':c.csrf});preview=json.loads(c.opener.open(request).read());sid=preview['definitions'][0]['definition']['Id'];c.call('/api/sequence/apply',dict(token=preview['token'],ids=[sid],mode='replace'))
                if was_live:c.call('/api/control',dict(action='start'))
                return sid
            sid=install();cfg=c.call('/api/sequence/config')
            def start(name,expected=200):
                body=dict(action='start',id=sid,revision=c.call('/api/sequence/config')['revision'],requestId=secrets.token_hex(16))
                if name is not None:body['runName']=name
                return body,c.call('/api/sequence/control',body,expected=expected)
            for name in [None,'   ','x'*129,'bad\nname']:start(name,400)
            check(not c.call('/api/sequence/config')['runtime']['running'],'sequence start requires a valid explicitly confirmed name')
            first_request,first=start('试验A/确认名称');send(.5);wait(lambda:not c.call('/api/sequence/state?id='+sid)['runtime']['running'],'named sequence complete');first_id='sequence:'+today+':'+first['runId'];one=detail(first_id)
            check(one['summary']['name']=='试验A/确认名称' and one['definition']['Name']=='试验A/确认名称' and len(one['definition']['Rows'])==2 and any(e['Event']=='结束' for e in one['events']),'a named run stores the complete original sequence snapshot and every result in its own file')
            check(c.call('/api/sequence/config')['definitions'][0]['Name']==cfg['definitions'][0]['Name'],'per-run naming does not rename the imported definition')
            replay=c.call('/api/sequence/control',first_request);check(replay['runId']==first['runId'],'retrying a named start does not create a second run or archive')
            sid=install('TM.A4.value==99','execute');_,second=start('试验B');wait(lambda:not c.call('/api/sequence/state?id='+sid)['runtime']['running'],'timed-out execute archive');second_id='sequence:'+today+':'+second['runId'];two=detail(second_id)
            check(two['summary']['type']=='sequence-execute' and two['summary']['abnormal']==1 and any(e['Status']=='超时' for e in two['events']),'execution timeouts and termination are retained in the individual run archive')
            sid=install();_,third=start('试验A/确认名称');send(.5);wait(lambda:not c.call('/api/sequence/state?id='+sid)['runtime']['running'],'second named sequence');check(listing(name='试验A/确认名称')['history']['total']==2 and len(list((data/'archives/sequences'/today).glob('*.jsonl')))==3,'same-name repeated runs stay separate and can be retrieved together by name')
            before_download=c.opener.open(c.base+'/api/judgment-archives/download?'+urlencode(dict(id=first_id))).read();check(all(json.loads(line)['RunId']==first['runId'] for line in before_download.decode('utf-8').splitlines()),'raw archive download contains only the selected run')
            csvdata=c.opener.open(c.base+'/api/judgment-archives/export.csv?'+urlencode(dict(id=first_id))).read();exported=list(csv.reader(io.StringIO(csvdata.decode('utf-8-sig'))));check(len(exported[0])==11 and all(r[1]=='试验A/确认名称' for r in exported[1:]),'per-run CSV exports the requested eleven columns with the confirmed work name')
            c.call('/api/control',dict(action='stop'));daily_before=path.read_bytes();process.terminate();process.wait(timeout=6);process=launch();c=Client(port);wait(ready,'archive restart');c.csrf=c.call('/api/auth/login',dict(username='archiveqa',password=password))['csrf'];check(path.read_bytes()==daily_before and c.opener.open(c.base+'/api/judgment-archives/download?'+urlencode(dict(id=first_id))).read()==before_download,'daily summaries and per-run archives survive restart without duplicated results or rewrites')
            check(c.call('/api/state')['mode']=='stopped' and not c.call('/api/sequence/config')['runtime']['running'],'archive restoration never starts reception or sequence execution')
            check(c.call('/api/command-send/history')['total']==0,'all verification used loopback telemetry and sent no device commands')
            process.terminate();process.wait(timeout=6);mirror=data/'archives/sequences'/today/(first['runId']+'.jsonl');mirror.write_bytes(before_download+b'{"Event":')
            process=launch();c=Client(port);wait(ready,'recover mirrored tail');c.csrf=c.call('/api/auth/login',dict(username='archiveqa',password=password))['csrf'];check(mirror.read_bytes()==before_download and list(mirror.parent.glob(mirror.name+'.recovered-*.bak')),'an interrupted per-run archive tail is backed up and recovered from the original audit')
            process.terminate();process.wait(timeout=6);mirror.write_bytes(b'\n'.join(before_download.splitlines()[:-1])+b'\n')
            process=launch();c=Client(port);wait(ready,'reconcile missing mirror event');c.csrf=c.call('/api/auth/login',dict(username='archiveqa',password=password))['csrf'];check([json.loads(line) for line in mirror.read_text(encoding='utf-8-sig').splitlines() if line]==[json.loads(line) for line in before_download.decode('utf-8-sig').splitlines() if line] and mirror.read_bytes().startswith(b'\n'.join(before_download.splitlines()[:-1])+b'\n'),'a missing event is appended once with identical content while the existing archive prefix remains byte-for-byte intact')
            process.terminate();process.wait(timeout=6);corrupt=before_download.replace(b'"Event":"',b'"Event":BROKEN,"',1);mirror.write_bytes(corrupt)
            process=launch();c=Client(port);wait(ready,'corrupt mirror protection');c.csrf=c.call('/api/auth/login',dict(username='archiveqa',password=password))['csrf'];check(listing()['error'] and mirror.read_bytes()==corrupt,'a corrupt complete archive is retained and reported instead of silently replaced')
            c.call('/api/control',dict(action='start'));c.call('/api/continuous-rules/control',dict(action='start'),expected=409);check(not c.call('/api/continuous-rules')['execution']['running'],'unavailable archives block new judgment runs rather than losing required records')
        finally:
            sender.close()
            if process.poll() is None:process.terminate();process.wait(timeout=6)
    print(f'All {len(checks)} judgment archive checks passed.')


if __name__=='__main__':main()
