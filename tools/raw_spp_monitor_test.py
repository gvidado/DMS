"""Independent raw SPP retention and decoding; isolated HTTP/loopback UDP only."""
import copy,csv,io,json,secrets,shutil,socket,struct,subprocess,sys,tempfile,time,urllib.request
from pathlib import Path
from integration_test import ROOT,Client,free_port,wait,packet,seal,seed_send_monitor,load_replay


def pcapng(payloads):
    def block(kind,body):
        body+=b'\0'*(-len(body)%4);size=len(body)+12;return struct.pack('<II',kind,size)+body+struct.pack('<I',size)
    out=[block(0x0a0d0d0a,struct.pack('<IHHq',0x1a2b3c4d,1,0,-1)),block(1,struct.pack('<HHI',101,0,65535))]
    for i,raw in enumerate(payloads):
        udp=struct.pack('>HHHH',5000,30008,len(raw)+8,0)+raw
        ip=struct.pack('>BBHHHBBH4s4s',0x45,0,len(udp)+20,0,0,64,17,0,socket.inet_aton('127.0.0.1'),socket.inet_aton('127.0.0.1'))+udp
        ticks=1700000000000000+i*1000;out.append(block(6,struct.pack('<IIIII',0,ticks>>32,ticks&0xffffffff,len(ip),len(ip))+ip))
    return b''.join(out)


def main():
    exe=Path(sys.argv[1]).resolve();checks=[]
    def check(ok,label):assert ok,label;checks.append(label);print('PASS',label,flush=True)
    with tempfile.TemporaryDirectory(prefix='raw-spp-',dir=ROOT/'test-output') as scratch:
        root=Path(scratch);shutil.copytree(ROOT/'config',root/'config');shutil.copytree(ROOT/'web',root/'web')
        for name in ['decoder.json','continuous-rules.json','tc-bindings.json','command-send.json','sequences.json','observed-commands.json']:(root/'config'/name).unlink(missing_ok=True)
        seed_send_monitor(root);udp=free_port(socket.SOCK_DGRAM);web=free_port();c=Client(web)
        settings=json.loads((root/'config/server.json').read_text(encoding='utf-8-sig'));settings.update(BindAddress='127.0.0.1',UdpPort=udp,SourceIPs=['127.0.0.1'],MulticastGroup='',MulticastInterface='',MappingConfirmed=True,DataTimeoutMs=60000)
        (root/'config/server.json').write_text(json.dumps(settings),encoding='utf-8');(root/'config/parameters.json').write_text('[]',encoding='utf-8')
        def definition(id,source):return dict(Id=id,Name='原包'+id,Enabled=False,MonitorRaw=True,OuterSourceId=source,SourceIP='127.0.0.1',Version=0,PacketType=0,SecondaryHeader=1,Apid=8,SequenceFlags=3,ExpectedLengthField=1017,ExpectedDatagramLength=0,SppOffset=6,PayloadOffset=16,StrictLength=True,ServiceType=3,ServiceSubtype=25,SourceAddress=4100,MappingConfirmed=False,Groups=[])
        a,b=definition('raw-a',0x0802),definition('raw-b',0x0202)
        config=dict(SchemaVersion=2,Revision='raw-fixture',Catalog=dict(Parameters=[],Sheets=[],Issues=[],Assumptions=[]),Packets=[a,b])
        (root/'config/decoder.json').write_text(json.dumps(config,ensure_ascii=False),encoding='utf-8')
        proc=None;sender=socket.socket(socket.AF_INET,socket.SOCK_DGRAM);sender.bind(('127.0.0.1',0))
        def launch():return subprocess.Popen([str(exe),'--local','--root',str(root),'--web-port',str(web)],stdout=subprocess.DEVNULL,stderr=subprocess.STDOUT,creationflags=subprocess.CREATE_NO_WINDOW)
        def ready():
            try:c.call('/api/auth/me');return True
            except OSError:return False
        def state():return c.call('/api/state')
        def rows(id='',page=1):return c.call('/api/raw-spp/list?packetId='+id+'&page='+str(page))
        def detail(id):return c.call('/api/raw-spp/detail?id='+str(id))
        def raw(source,seq,value=1):
            data=packet(seq);struct.pack_into('>HHH',data,0,source,0,len(data)-7);data[22]=value;return bytes(seal(data))
        def send(data,sock=sender):sock.sendto(data,('127.0.0.1',udp));time.sleep(.004)
        def save(packet,expected=200):return c.call('/api/decoders/packet',dict(packet=packet,revision=c.call('/api/decoders')['revision']),expected=expected)
        def replace(value):
            req=urllib.request.Request(c.base+'/api/decoders/preview?filename=raw.json',data=json.dumps(value).encode(),headers={'Content-Type':'application/octet-stream','X-CSRF-Token':c.csrf})
            preview=json.loads(c.opener.open(req).read());return c.call('/api/decoders/apply',dict(token=preview['token'],revision=preview['revision'],mode='replace'))
        try:
            proc=launch();wait(ready,'raw monitor server');password=secrets.token_urlsafe(18);c.call('/api/auth/setup',dict(username='rawqa',name='原包测试',password=password));c.csrf=c.call('/api/auth/login',dict(username='rawqa',password=password))['csrf']
            check(not c.call('/api/catalog') and len(rows()['packets'])==2,'raw-only SPPs can load and run without any telemetry table or mapping')
            anon=Client(web);anon.call('/api/raw-spp/list',expected=401);anon.call('/api/raw-spp/detail?id=1',expected=401);anon.call('/api/raw-spp/export.csv',expected=401);c.call('/api/raw-spp/clear',dict(packetId=''),expected=403,csrf=False)
            check(True,'raw lists, source detail, export and mutation require authentication/CSRF')
            c.call('/api/control',dict(action='start'));first=raw(0x0802,1);send(first);send(raw(0x0202,1));wait(lambda:rows()['total']==2,'two raw-only packets')
            r=rows('raw-a')['records'][0];d=detail(r['id']);check(bytes.fromhex(d['sppHex'])==first[6:] and bytes.fromhex(d['udpHex'])==first,'SPP and complete UDP bytes are retained exactly without decoding')
            check(state()['accepted']==0 and state()['invalid']==0 and state()['rawSppRetained']==2 and not state()['rows'],'raw-only traffic does not contaminate telemetry values or decoder rejection counters')
            send(first);wait(lambda:rows('raw-a')['total']==2,'raw duplicate retained');check(state()['duplicates']==0,'duplicate raw frames are retained independently of decoder duplicate filtering')
            bad=bytearray(raw(0x0802,2));bad[-1]^=1;send(bad);wait(lambda:rows('raw-a')['total']==3,'bad checksum retained');r=rows('raw-a')['records'][0]
            check(r['status']=='异常' and r['checksum']=='失败' and bytes.fromhex(detail(r['id'])['udpHex'])==bad,'bad-checksum raw packets remain complete with an explicit diagnostic')
            foreign=socket.socket(socket.AF_INET,socket.SOCK_DGRAM);foreign.bind(('127.0.0.3',0));send(first,foreign);foreign.close();check(rows()['total']==4 and state()['filtered']==1,'raw monitoring obeys the listener source-IP whitelist')
            for i in range(205):send(raw(0x0802,100+i,i%256))
            send(raw(0x0202,2));wait(lambda:rows('raw-a')['total']==200 and rows('raw-b')['total']==2,'per-packet retention')
            check(rows()['total']==202 and rows('raw-a')['records'][0]['sequence']==304,'each SPP has an independent 200-record ring instead of one shared global cap')
            oldest=rows('raw-a',4)['records'][-1];check(oldest['sequence']==105,'ring eviction removes only the oldest packet in the same SPP buffer')
            exported=c.opener.open(c.base+'/api/raw-spp/export.csv?packetId=raw-a').read();csvrows=list(csv.DictReader(io.StringIO(exported.decode('utf-8-sig'))))
            check(exported.startswith(b'\xef\xbb\xbf') and len(csvrows)==200 and all(v['SPP包ID']=='raw-a' for v in csvrows) and bytes.fromhex(csvrows[-1]['完整UDP载荷HEX'])==raw(0x0802,304,204),'filtered CSV exports every retained source byte with UTF-8 BOM')
            c.call('/api/control',dict(action='stop'));check(rows()['total']==202,'stopping reception preserves every raw ring');c.call('/api/control',dict(action='start'));check(rows()['total']==202,'starting a new listener session also preserves raw rings')
            c.call('/api/raw-spp/clear',dict(packetId='raw-a'));check(rows('raw-a')['total']==0 and rows('raw-b')['total']==2,'clear affects the selected packet only while other buffers remain intact')
            c.call('/api/control',dict(action='stop'));off=copy.deepcopy(a);off['MonitorRaw']=False;save(off);c.call('/api/control',dict(action='start'));send(raw(0x0802,400));send(raw(0x0202,3));wait(lambda:rows('raw-b')['total']==3,'remaining raw active')
            check(rows('raw-a')['total']==0,'turning off raw monitoring stops collection without enabling telemetry decoding')
            c.call('/api/control',dict(action='stop'));c.call('/api/decoders/packet/delete',dict(id=b['Id'],revision=c.call('/api/decoders')['revision']))
            check(rows('raw-b')['total']==3 and not next(p for p in rows()['packets'] if p['id']=='raw-b')['enabled'],'removing or disabling a definition preserves previously collected bytes and filter access')
            incoming=c.call('/api/decoders')['configuration'];incoming['Catalog']['Parameters']=[dict(Id='A1',Code='A1',Name='参数一',Group='test',Offset=0,Length=1,Type='uint',Scale=1,Supported=True)];incoming['Packets']=[copy.deepcopy(a)]
            decode=copy.deepcopy(a);decode.update(Id='decode-a',Name='独立解码',Enabled=True,MonitorRaw=False,MappingConfirmed=True,Groups=['test']);incoming['Packets'].append(decode);replace(incoming)
            check(len(c.call('/api/decoders')['configuration']['Packets'])==2,'raw-only and decoder-only definitions may share routing fields independently')
            c.call('/api/control',dict(action='start'));send(raw(0x0802,500,9));wait(lambda:state()['accepted']==1 and rows('raw-a')['total']==1,'raw and decoding paths together')
            history=c.call('/api/telemetry/history?reference=TME.A1&sourceIP=127.0.0.1');check(history['current']['engineeringText']=='9','enabling telemetry in a separate definition still updates its engineering value')
            send(raw(0x0802,500,9));wait(lambda:state()['duplicates']==1 and rows('raw-a')['total']==2,'decoder duplicate and raw retain');check(state()['accepted']==1,'decoder rejects a duplicate while raw monitor independently retains it')
            c.call('/api/control',dict(action='stop'));save(off);c.call('/api/control',dict(action='start'));send(raw(0x0802,501,7));wait(lambda:state()['accepted']==1,'decoder-only still works');check(rows('raw-a')['total']==2,'switching off raw monitoring does not stop an enabled telemetry decoder')
            c.call('/api/control',dict(action='stop'));decode['MonitorRaw']=True;save(decode);duplicate=copy.deepcopy(a);duplicate['Id']='duplicate-raw';check('监测原包' in save(duplicate,400)['error'],'overlapping definitions in the raw lane are rejected without touching the decoder lane')
            c.call('/api/control',dict(action='start'));send(raw(0x0802,600,6));wait(lambda:rows('decode-a')['total']==1 and state()['accepted']==1,'both switches on one definition')
            check(True,'one SPP definition may enable both monitoring and telemetry decoding')
            c.call('/api/control',dict(action='stop'))
            req=urllib.request.Request(c.base+'/api/captures/start?filename=combined.pcapng&includeDisabled=false',data=pcapng([raw(0x0802,650,6)]),headers={'Content-Type':'application/octet-stream','X-CSRF-Token':c.csrf})
            job=json.loads(c.opener.open(req).read())['id'];wait(lambda:c.call('/api/captures/status?id='+job)['state']=='completed','enabled decoder file verification')
            verified=c.call('/api/captures/status?id='+job+'&full=true');check(verified['summary']['decoded']==1 and verified['summary']['valid']==1,'file verification still decodes telemetry when the decoder switch is enabled')
            decode.update(Enabled=False,MappingConfirmed=False);save(decode);c.call('/api/raw-spp/clear',dict(packetId=''))
            load_replay(c,pcapng([raw(0x0802,700),raw(0x0802,701)]));c.call('/api/control',dict(action='demo'));wait(lambda:state()['replay']['state']=='completed','raw-only pcapng demo')
            check(rows('decode-a')['total']==2 and all(r['mode']=='文件演示' for r in rows('decode-a')['records']) and state()['accepted']==0,'pcapng demonstration feeds the same independent raw path without decoding')
            before_check=rows()['total'];capture=pcapng([raw(0x0802,800),raw(0x0802,801)])
            req=urllib.request.Request(c.base+'/api/captures/start?filename=raw-check.pcapng&includeDisabled=false',data=capture,headers={'Content-Type':'application/octet-stream','X-CSRF-Token':c.csrf})
            job=json.loads(c.opener.open(req).read())['id'];wait(lambda:c.call('/api/captures/status?id='+job)['state']=='completed','raw-only capture verification')
            report=c.call('/api/captures/status?id='+job+'&full=true');check(report['summary']['decoded']==0 and all(r['Status']=='原包监测' for r in report['records']),'file verification recognizes raw-only SPPs without decoding their internal parameters')
            check(rows()['total']==before_check,'read-only file verification never writes into the live raw ring')
            frame=c.call('/api/captures/detail?id='+job+'&frame=1');check(not frame['rows'] and bytes.fromhex(frame['hex'])==raw(0x0802,800),'file detail preserves raw-only payload instead of decoding disabled parameters')
            proc.terminate();proc.wait(timeout=8);proc=launch();wait(ready,'raw monitor restart');c.csrf=c.call('/api/auth/login',dict(username='rawqa',password=password))['csrf']
            check(rows()['total']==0 and any(p['enabled'] for p in rows()['packets']),'process restart deletes temporary raw bytes but retains monitoring configuration')
            (ROOT/'test-output/raw-spp-monitor/integration-report.json').write_text(json.dumps(dict(checks=len(checks),details=checks),ensure_ascii=False,indent=2),encoding='utf-8');print('All',len(checks),'raw monitor checks passed.')
        finally:
            sender.close()
            if proc:proc.terminate();proc.wait(timeout=8)
if __name__=='__main__':main()
