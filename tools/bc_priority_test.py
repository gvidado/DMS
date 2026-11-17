"""BC containment priority, structured fallback, TC and independent sequences. Loopback only."""
import csv,io,json,secrets,shutil,socket,subprocess,sys,tempfile,urllib.parse,urllib.request
from pathlib import Path
from integration_test import ROOT,Client,free_port,wait
from runtime_benchmark import seed
from stc_xml_import_test import envelope,xml
from sequence_test import xlsx

def main():
    exe=Path(sys.argv[1]).resolve();checks=[]
    def check(ok,label):assert ok,label;checks.append(label);print('PASS',label,flush=True)
    with tempfile.TemporaryDirectory(dir=ROOT/'test-output',prefix='bc-priority-') as temporary:
        root=Path(temporary);seed(root,per_packet=8,packets=1);shutil.copytree(ROOT/'web',root/'web')
        def save(name,value):(root/'config'/name).write_text(json.dumps(value),encoding='utf-8')
        settings=json.loads((root/'config/server.json').read_text('utf-8-sig'));settings['UdpPort']=free_port(socket.SOCK_DGRAM);save('server.json',settings)
        save('tc-bindings.json',dict(Revision='bc',Bindings=[dict(Number=1,Code='KBC')]))
        save('sequences.json',dict(Revision='bc',Sequences=[]))
        bc=bytes.fromhex('11011A08C000001130020A0000000000010800010D370D370736')
        broadcast=bytes.fromhex('1F011A70C000001130020A00000000000101000100050005DB7E')
        wrapped=b'\xFF\x00'+bc+b'\x55';data=bytes.fromhex('99223344');entries=[]
        definitions=[(1,'BC2001','BC',envelope(bc,0x1553,32)),(2,'BC0001','BC',envelope(broadcast,0x1553,32)),(3,'ZK0001','ZK',envelope(wrapped)),(4,'ZK0002','ZK',envelope(data)),(5,'ZK0003','ZK',envelope(data,222)),(6,'ZK0004','ZK',envelope(data,221,93)),(7,'ZK0005','ZK',envelope(data,counter=171)),(8,'ZK0010','ZK',b'\x01\x02\x03'),(9,'BC0099','BC',envelope(b'',0x1553,99)),(10,'BC2002','BC',envelope(bc,0x1553,32,counter=172))]
        for number,code,kind,raw in definitions:
            categories=[dict(Kind=kind,Code=code,DestinationIPs=['127.0.0.2'])]
            if number==1:categories.append(dict(Kind='ZK',Code='ZK0300',DestinationIPs=['127.0.0.2']))
            entries.append(dict(Number=number,Name=code+'原名称',STC=number!=8,DestinationIP='127.0.0.2',DestinationIPs=['127.0.0.2'],PayloadHex=raw.hex(' '),Categories=categories))
        save('observed-commands.json',dict(SchemaVersion=1,InterfaceWidthRule=2,SourceIP='127.0.0.1',NextNumber=11,Commands=entries))
        port=free_port();c=Client(port);password=secrets.token_urlsafe(18);log=(root/'server.log').open('wb');process=None
        target=socket.socket(socket.AF_INET,socket.SOCK_DGRAM);target.bind(('127.0.0.2',0));target.settimeout(3)
        other=socket.socket(socket.AF_INET,socket.SOCK_DGRAM);other.bind(('127.0.0.3',0));other.settimeout(3)
        def launch():return subprocess.Popen([str(exe),'--local','--root',str(root),'--web-port',str(port)],stdout=log,stderr=subprocess.STDOUT,creationflags=subprocess.CREATE_NO_WINDOW)
        def ready():
            try:c.call('/api/auth/me');return True
            except OSError:return False
        def upload(raw,fmt='stc-xml',**query):
            request=urllib.request.Request(c.base+'/api/send-monitor/commands/preview?'+urllib.parse.urlencode(dict(format=fmt,**query)),data=raw,headers={'Content-Type':'application/octet-stream','X-CSRF-Token':c.csrf})
            return json.loads(c.opener.open(request,timeout=20).read())
        def disk():return json.loads((root/'config/observed-commands.json').read_text('utf-8-sig'))
        def snapshot():return (root/'config/observed-commands.json').read_bytes()
        def csv_payload(rows):
            out=io.StringIO();csv.writer(out).writerows([['指令编号','指令名称','指令类别','来源IP','目的IP','完整UDP载荷HEX','指令格式']]+rows);return out.getvalue().encode('utf-8')
        def apply(p):return c.call('/api/send-monitor/commands/apply',dict(token=p['token']))
        def sequence_preview(rows):
            request=urllib.request.Request(c.base+'/api/sequence/preview?filename=bc-priority.xlsx&mode=judge&timeout=30&sourceIP=127.0.0.1',data=xlsx(rows),headers={'Content-Type':'application/octet-stream','X-CSRF-Token':c.csrf})
            return json.loads(c.opener.open(request).read())
        try:
            process=launch();wait(ready,'BC priority server');c.call('/api/auth/setup',dict(username='bcqa',name='BC优先验证',password=password));c.csrf=c.call('/api/auth/login',dict(username='bcqa',password=password))['csrf'];before=snapshot()
            nibble=bytes.fromhex('0'+bc.hex()+'0')
            batch=xml(('相同BC',221,92,bc.hex()),('包含BC',222,1,wrapped.hex()),('BC优先于头校验',70000,'bad',bc.hex()),('包含广播',221,92,('AA'+broadcast.hex()+'BB')),('仅部分BC',221,92,bc[:-1].hex()),('半字节不算',221,92,nibble.hex()),('普通新指令',223,92,data.hex()),('完整源码重复',221,92,data.hex()),('数据为空',70000,'bad',''))
            p=upload(batch,code='ZK0100');rows=p['xmlRows']
            check(p['canApply'] and not p['errors'] and p['skippedBC']==4 and p['skippedEmpty']==1 and p['skippedExisting']==1 and p['addedCommands']==3,'XML checks complete BC containment first and preserves the existing full-packet fallback')
            check(all(r['status']=='BC数据重复，跳过' for r in rows[:4]) and all('BC2001' in ' '.join(r['matches']) and 'BC2002' in ' '.join(r['matches']) for r in rows[:3]),'XML preview explains every BC match including duplicate full-packet variants')
            check(not rows[2]['errors'] and not rows[2]['packetHex'],'BC containment skips before function ID, interface and other new-packet validation')
            check([rows[i]['code'] for i in [4,5,6]]==['ZK0100','ZK0101','ZK0102'],'BC and empty/full-duplicate skips consume no ZK numbers')
            check(rows[4]['status']==rows[5]['status']=='待入库','partial bytes and a HEX substring at an odd nibble boundary do not match BC')
            p=c.call('/api/send-monitor/commands/stc-recheck',dict(token=p['token'],rows=[dict(index=5,name='网页修正名称',kind='ZK',code='ZK0100',destination='127.0.0.2')]))
            check(p['skippedBC']==4 and p['xmlRows'][4]['name']=='网页修正名称' and p['canApply'],'XML recheck retains BC priority and edits the intended accepted row')
            check(snapshot()==before,'XML read/recheck leaves existing BC names, packets and destinations untouched')
            c.call('/api/send-monitor/commands/preview/cancel',dict(token=p['token']));c.call('/api/send-monitor/commands/apply',dict(token=p['token']),expected=400)
            p=upload(batch,code='ZK0100');apply(p);check(len(disk()['Commands'])==13 and all(disk()['Commands'][i]['Name']==entries[i]['Name'] for i in range(10)),'XML apply adds only the three eligible records and preserves all original library entries')
            manual=c.call('/api/send-monitor/commands/preview?format=stc-manual',dict(name='手动保留完整UDP规则',bujianid='221',gongnengid='94',datastr=(b'\xEE'+bc+b'\xDD').hex(),destinationIP='127.0.0.2'))
            check(manual['canApply'] and manual['skippedBC']==0,'manual STC keeps its existing full-UDP import rule')
            manual=c.call('/api/send-monitor/commands/stc-recheck',dict(token=manual['token'],rows=[]));check(manual['canApply'] and manual['skippedBC']==0,'manual preview cannot acquire XML-only BC skipping through recheck')
            csv_raw=envelope(wrapped,221,94);p=upload(csv_payload([['ZK0200','CSV保留完整UDP','ZK','127.0.0.1','127.0.0.2',csv_raw.hex(' '),'STC']]),fmt='');check(p['canApply'] and p['addedCommands']==1,'CSV continues to compare the full packet even when it contains BC');apply(p)
            codes=['BC2001','ZK0001','ZK0002','ZK0003','BC0001','ZK0005','ZK0300','KBC']
            for row,code in zip(c.call('/api/continuous-rules')['rows'],codes):
                rule=row['rule'];rule.update(Enabled=True,Script='InitRegion this.state=0; EndInitRegion StateRegion if(TC.'+code+') then this.state=this.state+1; end EndStateRegion ResultRegion RET=Normal; EndResultRegion');c.call('/api/continuous-rules/save',dict(rule=rule,revision=c.call('/api/continuous-rules')['revision']))
            p=sequence_preview([('等待BC',[['','应仅BC完成','BC2001','','','.2','30']]),('等待ZK同一记录',[['','不能由BC误通过','ZK0300','','','.2','30']])]);ids=[r['definition']['Id'] for r in p['definitions']];c.call('/api/sequence/apply',dict(token=p['token'],ids=ids,mode='replace'))
            adapter=next(i for i in c.call('/api/send-monitor/interfaces')['interfaces'] if i['loopback']);c.call('/api/send-monitor/config',dict(SourceIPs=['127.0.0.1'],DestinationIPs=['127.0.0.2','127.0.0.3'],InterfaceId=adapter['id'],DestinationPort=0));c.call('/api/control',dict(action='start'));c.call('/api/continuous-rules/control',dict(action='start'))
            runs=[]
            for i,sid in enumerate(ids):runs.append(c.call('/api/sequence/control',dict(action='start',id=sid,revision=c.call('/api/sequence/config')['revision'],runName='优先级序列'+str(i),requestId=secrets.token_hex(16)))['runId'])
            wait(lambda:all('等待指令' in json.dumps(c.call('/api/sequence/state?runId='+run),ensure_ascii=False) for run in runs),'both sequence waiters')
            sender=socket.socket(socket.AF_INET,socket.SOCK_DGRAM);sender.bind(('127.0.0.1',0));sent=0
            def states():return [row['currentState'] for row in c.call('/api/continuous-rules')['rows']]
            def send(raw,receiver=target):
                nonlocal sent
                sender.sendto(raw,receiver.getsockname());assert receiver.recvfrom(65535)[0]==raw;sent+=1;wait(lambda:c.call('/api/send-monitor/state')['total']==sent,'captured physical packet')
            count=len(disk()['Commands']);originals={e['Number']:e['PayloadHex'] for e in disk()['Commands']}
            send(envelope(wrapped));wait(lambda:states()==[1,0,0,0,0,0,0,1],'BC-only TC dispatch')
            record=c.call('/api/send-monitor/list')['records'][0];check(set(record['commandCode'].split(' / '))=={'BC2001','BC2002'},'BC containment displays all matching BC codes and no ZK code')
            check(states()==[1,0,0,0,0,0,0,1] and c.call('/api/send-monitor/state')['total']==1,'BC and its manual alias trigger once per physical packet, without same-entry or separate-entry ZK events')
            wait(lambda:not c.call('/api/sequence/state?runId='+runs[0])['runtime']['running'],'BC sequence completes')
            check(c.call('/api/sequence/state?runId='+runs[0])['runtime']['abnormal']==0 and c.call('/api/sequence/state?runId='+runs[1])['runtime']['running'],'BC completes its own sequence and cannot complete a ZK alias waiting on the same library record')
            c.call('/api/sequence/control',dict(action='stop',runId=runs[1]))
            changed=bytearray(envelope(b'\xAA'+bc+b'\xBB',222,65535));changed[4:20]=bytes(range(16));changed[-4:]=b'\0'*4;send(bytes(changed));wait(lambda:states()[0]==2,'BC ignores outer metadata')
            check(len(disk()['Commands'])==count and states()[1]==states()[6]==0,'BC match ignores function ID, interface, counters and checksum without adding a duplicate ZK')
            send(envelope(wrapped),other);wait(lambda:states()[0]==3,'BC match at new destination')
            check(len(disk()['Commands'])==count and all('127.0.0.3' in e['DestinationIPs'] for e in disk()['Commands'] if e['Number'] in [1,10]),'contained BC matches update every original destination list without relying on the incoming data hash bucket')
            raw=bytearray(envelope(data));raw[4:20]=bytes(range(16));raw[-4:]=b'\0'*4;send(bytes(raw));wait(lambda:states()[2]==1 and states()[5]==1,'structured same-header variants')
            check(set(c.call('/api/send-monitor/list')['records'][0]['commandCode'].split(' / '))=={'ZK0002','ZK0005'} and states()[3]==0,'non-BC matches function ID, interface and complete data while still ignoring counter/checksum')
            send(envelope(data,222));wait(lambda:states()[3]==1,'other function ID');check(c.call('/api/send-monitor/list')['records'][0]['commandCode']=='ZK0003','different function IDs select different non-BC commands')
            send(envelope(data,221,93));check(c.call('/api/send-monitor/list')['records'][0]['commandCode']=='ZK0004','different interface types select different non-BC commands')
            send(envelope(data,223,91));check(len(disk()['Commands'])==count+1,'unknown function/interface combination cannot reuse a data-only non-BC match')
            send(b'\x01\x02\x03');check(c.call('/api/send-monitor/list')['records'][0]['commandCode']=='ZK0010','ordinary UDP continues to match the full raw payload')
            send(envelope(b'\x12'+broadcast+b'\x34'));wait(lambda:states()[4]==1,'broadcast containment');check(c.call('/api/send-monitor/list')['records'][0]['commandCode']=='BC0001','broadcast commands participate in BC priority')
            check(all(e['PayloadHex']==originals[e['Number']] for e in disk()['Commands'] if e['Number'] in originals),'passive matching preserves every stored full UDP source')
            records=list(csv.reader(io.StringIO(c.opener.open(c.base+'/api/send-monitor/export').read().decode('utf-8-sig'))));check(all('ZK' not in row[-2] for row in records[1:] if row[-2].startswith('BC')),'monitor CSV uses the same BC priority as the live display')
            sender.close();c.call('/api/control',dict(action='stop'));saved=snapshot();process.terminate();process.wait(10);c=Client(port);process=launch();wait(ready,'BC restart');c.csrf=c.call('/api/auth/login',dict(username='bcqa',password=password))['csrf']
            check(snapshot()==saved and len(disk()['Commands'])==count+1,'restart does not merge or delete old ZK/BC records')
            p=upload(xml(('重启后仍跳过',70000,'bad',wrapped.hex())));check(p['skippedBC']==1 and not p['errors'] and not p['canApply'],'BC priority and validation order survive restart')
            listing=c.call('/api/send-monitor/commands/list');c.call('/api/send-monitor/commands/edit',dict(number=1,originalCode='BC2001',code='BC2010',name='BC更新名称',revision=listing['revision']))
            p=upload(xml(('改名后匹配',221,92,wrapped.hex())));labels=' '.join(p['xmlRows'][0]['matches'])
            check('BC2010 BC更新名称' in labels and 'BC2001' not in labels and p['skippedBC']==1,'changing library metadata rebuilds the BC match index without stale names or codes')
            (exe.parent/'bc-priority-report.json').write_text(json.dumps(dict(checks=len(checks),details=checks),ensure_ascii=False,indent=2),encoding='utf-8')
        except Exception:
            log.flush();print((root/'server.log').read_text('utf-8',errors='replace')[-2000:]);raise
        finally:
            if process and process.poll() is None:process.terminate();process.wait(10)
            target.close();other.close();log.close()
    print('All',len(checks),'BC priority checks passed. Loopback only.')

if __name__=='__main__':main()
