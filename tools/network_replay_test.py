"""Real socket replay: omitted multicast membership and wrong filters must actually fail reception."""
import io
import json
import secrets
import shutil
import socket
import struct
import subprocess
import tempfile
import time
import urllib.request
from pathlib import Path
from integration_test import Client, ROOT, free_port, packet, seal, wait, seed_send_monitor
from capture_integration_test import section, epb, block


def main():
    checks=[]
    def check(ok,label):
        assert ok,label
        checks.append(label);print('PASS',label,flush=True)
    with tempfile.TemporaryDirectory(prefix='telemetry-network-replay-') as scratch:
        root=Path(scratch);shutil.copytree(ROOT/'config',root/'config'); seed_send_monitor(root);shutil.copytree(ROOT/'web',root/'web');(root/'config/decoder.json').unlink(missing_ok=True)
        port=free_port();udp_port=free_port(socket.SOCK_DGRAM);wrong_port=free_port(socket.SOCK_DGRAM);group='239.255.43.25'
        process=subprocess.Popen([str(ROOT/'TelemetryMonitor.exe'),'--local','--root',str(root),'--web-port',str(port)],stdout=subprocess.PIPE,stderr=subprocess.STDOUT)
        observer=None
        try:
            c=Client(port)
            def ready():
                try:return c.call('/api/auth/me')['setupRequired']
                except OSError:return False
            wait(ready,'network replay server');password=secrets.token_urlsafe(18);c.call('/api/auth/setup',dict(username='network_qa',name='网络收发测试',password=password));c.csrf=c.call('/api/auth/login',dict(username='network_qa',password=password))['csrf']
            interfaces=c.call('/api/network-replay/interfaces');local=next(n['ip'] for n in interfaces if n['multicast'] and not n['loopback'])
            original=next(ip for ip in ('192.0.2.113','198.51.100.113','203.0.113.113') if ip not in [n['ip'] for n in interfaces])
            def wire(payload,source_port=56964):
                udp=struct.pack('>HHHH',source_port,udp_port,len(payload)+8,0)+payload
                header=bytearray(struct.pack('>BBHHHBBH4s4s',0x45,0,len(udp)+20,1,0,64,17,0,socket.inet_aton(original),socket.inet_aton(group)))
                total=sum(struct.unpack('>10H',header));total=(total&65535)+(total>>16);total=(total&65535)+(total>>16);struct.pack_into('>H',header,10,(~total)&65535)
                return bytes.fromhex('01005e0000010011223344550800')+header+udp
            damaged=packet(2);damaged[-1]^=1;payloads=[packet(1),packet(1),damaged,packet(3)]
            capture=section()+b''.join(epb(wire(data),ticks=1_700_000_000_000000+t) for data,t in zip(payloads,(0,100000,200000,700000)))
            def upload(data,name='network.pcapng'):
                request=urllib.request.Request(c.base+'/api/network-replay/upload?filename='+name,data=data,headers={'Content-Type':'application/octet-stream','X-CSRF-Token':c.csrf})
                with c.opener.open(request,timeout=30) as response:return json.loads(response.read())
            def state():return c.call('/api/network-replay/state')
            def analyze(options,expected='prepared'):
                c.call('/api/network-replay/analyze',options);wait(lambda:state()['state'] in ('prepared','failed'),'network preflight');result=state();assert result['state']==expected,result;return result
            options=dict(Mode='local',LocalAddress=local,SourcePolicy='actual',DestinationFilter=group,PortFilter=udp_port,DestinationOverride='',DestinationPort=0,Speed=1,PreserveSourcePort=False)
            upload(capture);prepared=analyze(options);token=prepared['token']
            check(prepared['selected']==4 and prepared['sent']==0 and prepared['durationMs']==700 and local in prepared['flows'][0]['route'] and original in prepared['flows'][0]['route'],'preflight shows the original-to-actual route and complete packet count without sending or decoding')
            Client(port).call('/api/network-replay/state',expected=401);Client(port).call('/api/network-replay/interfaces',expected=401);c.call('/api/network-replay/start',dict(token=token),csrf=False,expected=403)
            check(True,'network replay status and interfaces require login and actual sending requires CSRF')
            cfg=c.call('/api/config');cfg.update(BindAddress='0.0.0.0',UdpPort=udp_port,MulticastGroup='',MulticastInterface='',SourceIPs=[original],MappingConfirmed=True,DataTimeoutMs=60000);c.call('/api/config',cfg);c.call('/api/selection',dict(ids=['工仪全程/A1']))
            def send():
                c.call('/api/network-replay/start',dict(token=token));wait(lambda:state()['state'] in ('completed','failed'),'network transmission');assert state()['state']=='completed',state()
            c.call('/api/control',dict(action='start'));send();time.sleep(.1)
            check(state()['sent']==4 and c.call('/api/state')['received']==0,'real replay sends all packets but a listener that has not joined the multicast group receives zero')
            c.call('/api/control',dict(action='stop'));cfg.update(MulticastGroup=group,MulticastInterface=local);c.call('/api/config',cfg);c.call('/api/control',dict(action='start'));send();wait(lambda:c.call('/api/state')['received']==4,'multicast packets before whitelist')
            observed=c.call('/api/state');check(observed['filtered']==4 and observed['accepted']==0,'joining multicast is insufficient when the actual sender IP is missing from the normal whitelist')
            c.call('/api/control',dict(action='stop'));cfg.update(UdpPort=wrong_port,SourceIPs=[local]);c.call('/api/config',cfg);c.call('/api/control',dict(action='start'));send();time.sleep(.1)
            check(c.call('/api/state')['received']==0,'a wrong listener destination port is not silently repaired or bypassed by the replay sender')
            c.call('/api/control',dict(action='stop'));cfg['UdpPort']=udp_port;c.call('/api/config',cfg);c.call('/api/control',dict(action='start'))
            observer=socket.socket(socket.AF_INET,socket.SOCK_DGRAM);observer.setsockopt(socket.SOL_SOCKET,socket.SO_REUSEADDR,1);observer.bind(('0.0.0.0',udp_port));observer.setsockopt(socket.IPPROTO_IP,socket.IP_ADD_MEMBERSHIP,socket.inet_aton(group)+socket.inet_aton(local));observer.settimeout(3)
            begin=time.monotonic();send();elapsed=time.monotonic()-begin;raw=[observer.recvfrom(65535) for _ in payloads];wait(lambda:c.call('/api/state')['received']==4,'normal receiver decode');observed=c.call('/api/state')
            check([r[0] for r in raw]==payloads and all(r[1][0]==local for r in raw),'an independent socket sees every unchanged UDP payload, including duplicates and bad SPP checksums, from the real sender IP')
            check(.6<elapsed<1.8 and observed['mode']=='live' and observed['accepted']==2 and observed['duplicates']==1 and observed['invalid']==1 and observed['rows'][0]['value']=='12.3456','correct multicast, port and whitelist reach normal live decoding at the original 1x timing')
            check(observed['packetStatistics'][0]['received']==4 and observed['packetStatistics'][0]['duplicates']==1 and any(r['mode']=='实时UDP' for r in c.call('/api/live-abnormal/list')['records']),'normal per-SPP counts and real-UDP anomaly records are updated by the actual receive thread')
            check(state()['actualBindings'] and state()['sentBytes']==sum(len(p) for p in payloads),'sender status exposes the actual bound source ports and transmitted byte count')
            c.call('/api/network-replay/speed',dict(speed=5));c.call('/api/control',dict(action='stop'));c.call('/api/control',dict(action='start'));begin=time.monotonic();send();elapsed=time.monotonic()-begin
            check(.08<elapsed<.65 and c.call('/api/state')['received']==4,'5x real UDP replay compresses packet timing without skipping any selected frame')
            c.call('/api/network-replay/speed',dict(speed=1));c.call('/api/control',dict(action='stop'));c.call('/api/network-replay/start',dict(token=token));c.call('/api/control',dict(action='demo'),expected=409);c.call('/api/network-replay/stop',{})
            check(c.call('/api/state')['mode']=='stopped','internal decoder preview cannot be started as a false receiver while real network sending is active')
            observer.close();observer=None
            failed=analyze(dict(options,SourcePolicy='original'),'failed');check('不是本机地址' in failed['error'] and failed['sent']==0 and not failed['canStart'],'preserving an unassigned captured source IP fails honestly before any packet is transmitted')
            failed=analyze(dict(options,DestinationOverride='198.51.100.99'),'failed');check('本机闭环' in failed['error'] and failed['sent']==0,'local-only mode rejects a destination on another computer before sending')
            failed=analyze(dict(options,PortFilter=wrong_port),'failed');check('没有符合' in failed['error'],'an incorrect capture port filter produces an explicit no-matching-UDP result')
            prepared=analyze(options);c.call('/api/network-replay/start',dict(token=token),expected=409);token=prepared['token']
            check(True,'a changed preflight plan invalidates stale send tokens')
            gap=section()+epb(wire(packet(1)),ticks=1000000)+epb(wire(packet(2)),ticks=121000000);upload(gap);prepared=analyze(options);token=prepared['token'];c.call('/api/control',dict(action='start'));c.call('/api/network-replay/start',dict(token=token));wait(lambda:state()['sent']==1,'first long-gap packet');begin=time.monotonic();c.call('/api/network-replay/stop',{})
            check(time.monotonic()-begin<.8 and c.call('/api/state')['mode']=='live','a long-gap sender stops promptly without stopping the independent live receiver')
            large=section()+block(0xdeadbeef,b'\0'*4096)*2200+epb(wire(packet(1)));upload(large,'large-network.pcapng');prepared=analyze(options)
            check(state()['size']>8*1024*1024 and prepared['selected']==1,'network source upload and streaming preflight exceed the old 8MB configuration-import limit')
            request=urllib.request.Request(c.base+'/api/captures/start?filename=reused-network.pcapng',data=capture,headers={'Content-Type':'application/octet-stream','X-CSRF-Token':c.csrf});job=json.loads(c.opener.open(request).read())['id'];wait(lambda:c.call('/api/captures/status?id='+job)['state']=='completed','capture result for sender reuse')
            c.call('/api/network-replay/use-capture?id='+job,{});c.call('/api/captures/delete?id='+job,{});prepared=analyze(options);token=prepared['token'];send()
            check(state()['sent']==4,'a checked capture is reused without copying and remains readable after its check result is cleared')
            c.call('/api/control',dict(action='stop'));c.call('/api/network-replay/clear',{});check(not state()['ready'] and not list((root/'data/network-replay').iterdir()),'clearing the network source releases retained temporary upload files')
            upload(capture);process.terminate();process.wait(timeout=6);check(not list((root/'data/network-replay').iterdir()),'process exit deletes the temporary network source while existing persistent alarm history remains separate')
        finally:
            if observer:observer.close()
            if process.poll() is None:process.terminate();process.wait(timeout=6)
    print('All',len(checks),'real network replay checks passed. Multicast fixture TTL=0 stays on this host.')


if __name__=='__main__':main()
