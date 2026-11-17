"""Real UDP sends only to loopback, using isolated accounts/configuration."""
import csv,io,json,secrets,shutil,socket,struct,subprocess,sys,tempfile
from pathlib import Path
from integration_test import ROOT,Client,free_port,wait

def payload():
    b=bytearray.fromhex('EB90EB9000000000AA00010000000100000000005315200000001104')+b'\x00\xaa\xff\x01'
    struct.pack_into('<I',b,4,len(b)-4);return bytes(b)+struct.pack('<I',sum(b))

def main():
    exe=Path(sys.argv[1]).resolve();checks=[]
    def check(ok,name):assert ok,name;checks.append(name);print('PASS',name,flush=True)
    with tempfile.TemporaryDirectory(prefix='sender-http-',dir=ROOT/'test-output') as temp:
        root=Path(temp);shutil.copytree(ROOT/'config',root/'config');shutil.copytree(ROOT/'web',root/'web')
        for name in ['decoder.json','continuous-rules.json','tc-bindings.json','command-send.json']:(root/'config'/name).unlink(missing_ok=True)
        raw=payload();(root/'config/observed-commands.json').write_text(json.dumps(dict(SchemaVersion=1,SourceIP='127.0.0.1',IdentityRule='udp-payload',NextNumber=2,Commands=[dict(Number=1,Name='环回注入测试',DestinationIP='127.0.0.2',DestinationIPs=['127.0.0.2','127.0.0.3'],PayloadHex=raw.hex(' '),Categories=[dict(Kind='YKE',Code='YKE0001',DestinationIPs=['127.0.0.2','127.0.0.3'])])]),ensure_ascii=False),encoding='utf-8')
        receivers=[]
        for address in ['127.0.0.2','127.0.0.3']:
            s=socket.socket(socket.AF_INET,socket.SOCK_DGRAM);s.bind((address,0));s.settimeout(.25);receivers.append(s)
        ports=[free_port(socket.SOCK_DGRAM),free_port(socket.SOCK_DGRAM)];port=free_port();proc=None;c=Client(port)
        def start():
            nonlocal proc
            proc=subprocess.Popen([str(exe),'--local','--root',str(root),'--web-port',str(port)],stdout=subprocess.DEVNULL,stderr=subprocess.STDOUT)
            def ready():
                try:c.call('/api/auth/me');return True
                except OSError:return False
            wait(ready,'sender server')
        def empty():
            for s in receivers:
                try:s.recvfrom(65535);raise AssertionError('unexpected UDP transmission')
                except socket.timeout:pass
        def preview(destinations=None):
            body=dict(number=1,code='YKE0001')
            if destinations is not None:body['destinations']=destinations
            return c.call('/api/command-send/preview',body)
        def save(config):
            current=c.call('/api/command-send/config');return c.call('/api/command-send/config',dict(revision=current['revision'],configuration=config))
        def send(p,expected=200):return c.call('/api/command-send/send',dict(token=p['token']),expected=expected)
        try:
            start();c.call('/api/command-send/config',expected=401);password=secrets.token_urlsafe(18);c.call('/api/auth/setup',dict(username='send_qa',name='发送测试',password=password));c.csrf=c.call('/api/auth/login',dict(username='send_qa',password=password))['csrf']
            default=c.call('/api/command-send/config');routes=default['configuration']['Routes'];check([(r['DestinationIP'],r['SourcePort'],r['DestinationPort']) for r in routes]==[('192.168.1.111',53333,31000),('192.168.1.112',53335,31000),('192.168.1.113',53334,31000),('192.168.1.137',8080,8080)],'four destination/source-port/receiver-port defaults match request')
            config=dict(SourceIP='127.0.0.1',Routes=[dict(DestinationIP=s.getsockname()[0],SourcePort=ports[i],DestinationPort=s.getsockname()[1]) for i,s in enumerate(receivers)]);save(config)
            c.call('/api/command-send/config',dict(revision=default['revision'],configuration=config),expected=409);check(True,'concurrent settings revision cannot overwrite saved mapping')
            p=preview();empty();check(p['canSend'] and bytes.fromhex(p['hex'])==raw,'preview reads exact catalog bytes and sends no UDP')
            check(p['dataContentRecognized'] and bytes.fromhex(p['dataContentHex'])==raw[24:-4] and p['dataContentLength']==len(raw)-28,'preview data area includes routing prefix and excludes outer header/checksum')
            c.call('/api/command-send/send',dict(token=p['token']),csrf=False,expected=403);empty();result=send(p)
            received=[s.recvfrom(65535) for s in receivers];check(result['ok'] and result['succeeded']==2 and all(data==raw and source==('127.0.0.1',ports[i]) for i,(data,source) in enumerate(received)),'real multi-destination UDP preserves every byte and binds each configured source port')
            send(p,400);empty();check(True,'single-use send token prevents duplicate request replay')
            check(c.call('/api/state')['sendMonitor']['total']==0,'manual sending works without reception and does not synthesize passive capture counts')
            p=preview(['127.0.0.2']);result=send(p);check(result['succeeded']==1 and receivers[0].recvfrom(65535)[0]==raw,'destination subset sends exactly one datagram');empty()
            c.call('/api/command-send/preview',dict(number=1,code='YKE0001',destinations=['127.0.0.4']),expected=400);check(True,'cannot override destination outside the selected catalog entry')
            blocker=socket.socket(socket.AF_INET,socket.SOCK_DGRAM)
            if hasattr(socket,'SO_EXCLUSIVEADDRUSE'):blocker.setsockopt(socket.SOL_SOCKET,socket.SO_EXCLUSIVEADDRUSE,1)
            blocker.bind(('127.0.0.1',ports[1]))
            try:result=send(preview());check(not result['ok'] and result['failed']==2 and all('占用' in r['error'] for r in result['rows']),'busy source port rejects complete multi-destination batch before any packet is sent');empty()
            finally:blocker.close()
            tcp=socket.socket(socket.AF_INET,socket.SOCK_STREAM);tcp.bind(('127.0.0.1',ports[0]));tcp.listen(1)
            try:result=send(preview(['127.0.0.2']));check(result['ok'] and receivers[0].recvfrom(65535)[1][1]==ports[0],'TCP and UDP may bind the same numeric port independently')
            finally:tcp.close()
            p=preview();save(config);send(p,400);empty();check(True,'saving route mapping invalidates older send preview')
            p=preview();exported=c.opener.open(c.base+'/api/send-monitor/commands/export.csv').read();rows=list(csv.reader(io.StringIO(exported.decode('utf-8-sig'))));rows[1][1]='新指令名称';out=io.StringIO();csv.writer(out).writerows(rows)
            import urllib.request
            request=urllib.request.Request(c.base+'/api/send-monitor/commands/preview',data=out.getvalue().encode('utf-8'),headers={'Content-Type':'text/csv','X-CSRF-Token':c.csrf});plan=json.loads(c.opener.open(request).read());c.call('/api/send-monitor/commands/apply',dict(token=plan['token']));send(p,409);empty();check(True,'catalog changes invalidate a previously reviewed send')
            history=c.call('/api/command-send/history');check(history['total']==6 and history['succeeded']==4 and history['failed']==2 and all(r['actor']=='发送测试' for r in history['rows']),'execution audit retains per-destination successes/failures and operator')
            raw_csv=c.opener.open(c.base+'/api/command-send/export.csv').read();csv_rows=list(csv.reader(io.StringIO(raw_csv.decode('utf-8-sig'))));check(len(csv_rows)==7 and all(bytes.fromhex(r[-1])==raw for r in csv_rows[1:]),'execution CSV contains each exact catalog payload and send result')
            settings=c.call('/api/config');settings.update(BindAddress='127.0.0.1',UdpPort=free_port(socket.SOCK_DGRAM),SourceIPs=['127.0.0.1'],MappingConfirmed=True,MulticastGroup='',MulticastInterface='');c.call('/api/config',settings)
            c.call('/api/send-monitor/config',dict(SourceIPs=['127.0.0.1'],DestinationIPs=['127.0.0.2','127.0.0.3'],InterfaceId='',DestinationPort=0))
            rules=c.call('/api/continuous-rules');rule=next((r['rule'] for r in rules['rows'] if r['rule']['Code']=='A1'),dict(Code='A1',Name='YKE事件测试',Level='一般',ParameterId=next(p['Id'] for p in c.call('/api/catalog') if p['Code']=='A1')));rule.update(Enabled=True,SourceIP='127.0.0.1',Script='InitRegion this.state=0; EndInitRegion StateRegion if(TC.YKE0001) then this.state=this.state+1; end EndStateRegion ResultRegion RET=Normal; EndResultRegion');c.call('/api/continuous-rules/save',dict(rule=rule,revision=rules['revision']))
            c.call('/api/control',dict(action='start'));c.call('/api/continuous-rules/control',dict(action='start'));send(preview(['127.0.0.2']));receivers[0].recvfrom(65535)
            wait(lambda:c.call('/api/state')['sendMonitor']['total']==1,'one real captured outgoing command');wait(lambda:next(r['currentState'] for r in c.call('/api/continuous-rules')['rows'] if r['rule']['Code']=='A1')==1,'one YKE event from real capture');check(c.call('/api/send-monitor/list')['records'][0]['commandCode']=='YKE0001','manual sender integrates with real Npcap counter, library display and exactly one TC event');c.call('/api/control',dict(action='stop'))
            proc.terminate();proc.wait(timeout=8);start();c.csrf=c.call('/api/auth/login',dict(username='send_qa',password=password))['csrf'];check(c.call('/api/command-send/config')['configuration']==config and c.call('/api/command-send/history')['total']==0,'route settings persist; temporary send history clears on process restart')
        finally:
            if proc and proc.poll() is None:proc.terminate();proc.wait(timeout=8)
            for s in receivers:s.close()
    print('All',len(checks),'command sender checks passed. No device IP received a test packet.')
if __name__=='__main__':main()
