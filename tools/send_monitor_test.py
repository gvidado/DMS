"""Real installed Npcap capture of UDP from independent sockets; sends only to loopback."""
import csv
import io
import secrets
import shutil
import socket
import subprocess
import tempfile
import time
import sys
from pathlib import Path
from integration_test import Client, ROOT, free_port, wait, packet
csv.field_size_limit(8*1024*1024)


def main():
    executable=Path(sys.argv[1]).resolve() if len(sys.argv)>1 else ROOT/'TelemetryMonitor.exe'
    checks=[]
    def check(ok,name):
        assert ok,name
        checks.append(name);print('PASS',name,flush=True)
    with tempfile.TemporaryDirectory(prefix='telemetry-send-monitor-') as temp:
        root=Path(temp);shutil.copytree(ROOT/'config',root/'config');shutil.copytree(ROOT/'web',root/'web')
        (root/'config/decoder.json').unlink(missing_ok=True);(root/'config/send-monitor.json').unlink(missing_ok=True)
        port=free_port();process=None
        def launch():
            return subprocess.Popen([str(executable),'--local','--root',str(root),'--web-port',str(port)],stdout=subprocess.PIPE,stderr=subprocess.STDOUT)
        def ready():
            try:return c.call('/api/auth/me')
            except OSError:return False
        c=Client(port);process=launch()
        try:
            wait(ready,'send monitor startup');password=secrets.token_urlsafe(18)
            c.call('/api/auth/setup',dict(username='send_qa',name='发送抓包测试',password=password));c.csrf=c.call('/api/auth/login',dict(username='send_qa',password=password))['csrf']
            state=lambda:c.call('/api/send-monitor/state')
            action=lambda name:c.call('/api/send-monitor/control' if name=='clear' else '/api/control',dict(action=name))
            anon=Client(port);anon.call('/api/send-monitor/interfaces',expected=401);anon.call('/api/send-monitor/export',expected=401)
            c.call('/api/send-monitor/control',dict(action='start'),expected=403,csrf=False)
            check(True,'capture interfaces and CSV require login; control requires CSRF')
            interfaces=c.call('/api/send-monitor/interfaces');assert interfaces['available'],interfaces
            adapter=next(a for a in interfaces['interfaces'] if a['loopback'] and '127.0.0.1' in a['addresses'])
            check(bool(interfaces['library']) and adapter['id'],'installed Npcap loads and enumerates the real loopback capture adapter')
            options=dict(SourceIP='127.0.0.1',DestinationIP='127.0.0.2',InterfaceId=adapter['id'],DestinationPort=0)
            c.call('/api/send-monitor/config',options)
            telemetry=c.call('/api/config');telemetry.update(BindAddress='127.0.0.1',UdpPort=free_port(socket.SOCK_DGRAM),SourceIPs=['127.0.0.1'],MulticastGroup='',MulticastInterface='',MappingConfirmed=True,DataTimeoutMs=60000)
            c.call('/api/config',telemetry)
            with socket.socket(socket.AF_INET,socket.SOCK_DGRAM) as receiver, socket.socket(socket.AF_INET,socket.SOCK_DGRAM) as sender:
                receiver.bind(('127.0.0.2',0));receiver.settimeout(3);destination=receiver.getsockname();sender.bind(('127.0.0.1',0))
                def send(data):sender.sendto(data,destination);assert receiver.recvfrom(65535)[0]==data
                action('start');wait(lambda:state()['state']=='running','capture ready');send(b'\x01\x02test-command')
                wait(lambda:state()['total']==1,'first real outgoing datagram')
                check(c.call('/api/state')['mode']=='live' and c.call('/api/state')['received']==0,'one main start activates UDP reception and passive capture; capture does not inject the telemetry decoder')
                check(all(state()['configuration'][k]==v for k,v in options.items()) and state()['configuration']['SourceIPs']==['127.0.0.1'] and state()['configuration']['DestinationIPs']==['127.0.0.2'],'legacy single-IP settings normalize to one-item lists and preserve port and interface')
                c.call('/api/send-monitor/config',options,expected=409);c.call('/api/send-monitor/control',dict(action='clear'),expected=409)
                check(state()['total']==1,'running capture rejects configuration changes and clearing without losing current records')
                with socket.socket(socket.AF_INET,socket.SOCK_DGRAM) as wrong:
                    wrong.bind(('127.0.0.2',0));wrong.sendto(b'wrong-source',destination);receiver.recvfrom(65535)
                sender.sendto(b'wrong-target',('127.0.0.3',destination[1]));time.sleep(.25)
                check(state()['total']==1,'opposite or unrelated IP-A/IP-B traffic is excluded by the capture filter')
                send(b'\x01\x02test-command');wait(lambda:state()['total']==2,'repeat command')
                check(state()['total']==2,'sending identical UDP contents twice increments the count twice')
                expected={1:b'\x01\x02test-command',2:b'\x01\x02test-command'}
                for i in range(3,228):expected[i]=bytes([i])+b'\x00\xff';send(expected[i]);time.sleep(.003)
                wait(lambda:state()['total']==227,'227 sent datagrams')
                records=c.call('/api/send-monitor/list')['records']
                check(state()['retained']==200 and state()['capacity']==200 and [r['id'] for r in records]==list(range(227,27,-1)),'total grows past 200 while the rolling window keeps exactly the newest 200 in reverse order')
                detail=c.call('/api/send-monitor/detail?id=227');check(bytes.fromhex(detail['udpHex'])==expected[227] and len(bytes.fromhex(detail['frameHex']))>len(expected[227]),'record detail retains exact UDP contents and the entire captured network frame')
                c.call('/api/send-monitor/detail?id=27',expected=400);check(bytes.fromhex(c.call('/api/send-monitor/detail?id=28')['udpHex'])==expected[28],'the 200th recent record remains available and the 201st is evicted')
                c.call('/api/send-monitor/detail?id=1',expected=400);check(True,'an evicted record is reported as unavailable')
                send(b'');expected[228]=b'';large=bytes(range(256))*200;send(large);expected[229]=large
                wait(lambda:state()['total']==229,'empty and 51200-byte loopback payloads')
                check(c.call('/api/send-monitor/detail?id=228')['record']['length']==0 and bytes.fromhex(c.call('/api/send-monitor/detail?id=229')['udpHex'])==large,'empty UDP and a 51200-byte payload are captured without truncation')
                with c.opener.open(c.base+'/api/send-monitor/export') as response:raw=response.read()
                rows=list(csv.DictReader(io.StringIO(raw.decode('utf-8-sig'))))
                check(raw.startswith(b'\xef\xbb\xbf') and len(rows)==200 and [int(r['记录号']) for r in rows]==list(range(229,29,-1)) and all(bytes.fromhex(r['完整UDP载荷HEX'])==expected[int(r['记录号'])] for r in rows),'CSV is UTF-8 BOM and exports exactly the newest 200 complete payloads, including large and empty packets')
                action('stop');before=state()['total'];send(b'after-stop');time.sleep(.15)
                check(state()['state']=='stopped' and state()['total']==before and len(c.call('/api/send-monitor/list')['records'])==200,'stopping capture preserves totals and records and does not count subsequent traffic')
                action('start');check(state()['total']==0 and state()['retained']==0,'successfully restarting capture begins a new counting session')
                send(b'new-session');wait(lambda:state()['total']==1,'new session packet')
                c.call('/api/send-monitor/detail?id=1',expected=400)
                check(True,'record IDs are not reused within the process when starting a new counting session')
                sender.sendto(packet(1),('127.0.0.1',telemetry['UdpPort']))
                wait(lambda:c.call('/api/state')['accepted']==1,'parallel telemetry decode');action('stop');send(b'after-main-stop');time.sleep(.1)
                check(state()['state']=='stopped' and c.call('/api/state')['mode']=='stopped' and state()['total']==1,'one main stop stops both reception and capture and preserves the session records')
                with socket.socket(socket.AF_INET,socket.SOCK_DGRAM) as occupied:
                    occupied.bind(('127.0.0.1',telemetry['UdpPort']));c.call('/api/control',dict(action='start'),expected=409)
                check(state()['state']=='stopped' and state()['total']==1 and c.call('/api/state')['mode']=='stopped','a UDP bind failure never starts capture or discards its previous counting session')
                options['DestinationPort']=destination[1];c.call('/api/send-monitor/config',options);action('start')
                sender.sendto(b'wrong-port',('127.0.0.2',destination[1]+1 if destination[1]<65535 else destination[1]-1));send(b'right-port');wait(lambda:state()['total']==1,'port filter match');time.sleep(.1)
                check(state()['total']==1,'optional UDP destination port filter excludes other ports')
                action('stop');action('clear');check(state()['total']==0 and state()['retained']==0,'manual clear resets both total and recent records')
                bad=dict(options,SourceIP='192.0.2.16');c.call('/api/send-monitor/config',bad);failure=c.call('/api/send-monitor/control',dict(action='start'),expected=409)
                check('网卡' in failure['error'] and state()['state']=='stopped' and c.call('/api/state')['mode']=='stopped','a capture startup failure rolls UDP reception back to stopped')
                with socket.socket(socket.AF_INET,socket.SOCK_DGRAM) as released:released.bind(('127.0.0.1',telemetry['UdpPort']))
                check(True,'capture startup rollback releases the UDP port immediately')
                multi=dict(SourceIPs=['127.0.0.1','127.0.0.4','127.0.0.1'],DestinationIPs=['127.0.0.2','127.0.0.3','127.0.0.2'],InterfaceId=adapter['id'],DestinationPort=0)
                c.call('/api/send-monitor/config',multi)
                normalized=state()['configuration']
                check(normalized['SourceIPs']==['127.0.0.1','127.0.0.4'] and normalized['DestinationIPs']==['127.0.0.2','127.0.0.3'],'multiple source and destination lists are normalized and deduplicated')
                action('start')
                with socket.socket(socket.AF_INET,socket.SOCK_DGRAM) as sender2, socket.socket(socket.AF_INET,socket.SOCK_DGRAM) as receiver3, socket.socket(socket.AF_INET,socket.SOCK_DGRAM) as outside:
                    sender2.bind(('127.0.0.4',0));receiver3.bind(('127.0.0.3',destination[1]));receiver3.settimeout(3);outside.bind(('127.0.0.5',0))
                    for source_socket,source_ip in [(sender,'127.0.0.1'),(sender2,'127.0.0.4')]:
                        for target_socket,target_ip in [(receiver,'127.0.0.2'),(receiver3,'127.0.0.3')]:
                            content=(source_ip+' to '+target_ip).encode();source_socket.sendto(content,(target_ip,destination[1]));assert target_socket.recvfrom(65535)[0]==content
                    wait(lambda:state()['total']==4,'four source-destination combinations')
                    outside.sendto(b'outside-source',destination);receiver.recvfrom(65535);sender.sendto(b'outside-destination',('127.0.0.6',destination[1]));time.sleep(.15)
                    check(state()['total']==4,'real capture matches all four listed combinations exactly once and excludes unlisted endpoints')
                action('stop')
                with c.opener.open(c.base+'/api/send-monitor/export') as response:multi_rows=list(csv.DictReader(io.StringIO(response.read().decode('utf-8-sig'))))
                check({(r['来源IP'],r['目的IP']) for r in multi_rows}=={(a,b) for a in ('127.0.0.1','127.0.0.4') for b in ('127.0.0.2','127.0.0.3')},'multi-IP CSV preserves the actual source and destination of every packet')
                for invalid in [dict(multi,SourceIPs=[]),dict(multi,DestinationIPs=[]),dict(multi,DestinationIPs=['not-an-ip']),dict(multi,DestinationIPs=['192.0.2.'+str(i) for i in range(1,66)])]:
                    c.call('/api/send-monitor/config',invalid,expected=400)
                check(state()['total']==4 and state()['configuration']==normalized,'empty, invalid and oversized address lists are rejected without changing records or saved configuration')
                c.call('/api/send-monitor/config',dict(multi,SourceIPs=['127.0.0.1','192.0.2.16']));failure=c.call('/api/control',dict(action='start'),expected=409)
                check('网卡' in failure['error'] and c.call('/api/state')['mode']=='stopped' and state()['state']=='stopped','any source outside the selected adapter prevents the entire combined start')
                c.call('/api/send-monitor/config',options);action('start');send(b'persist-check');wait(lambda:state()['total']==1,'pre-restart packet')
                process.terminate();process.wait(timeout=5);process=launch();wait(ready,'restarted server');c.csrf=c.call('/api/auth/login',dict(username='send_qa',password=password))['csrf']
                check(all(state()['configuration'][k]==v for k,v in options.items()) and state()['total']==0 and state()['retained']==0 and state()['state']=='stopped','configuration survives process restart; counts and packet contents are memory-only and capture does not autostart')
                check(c.call('/api/state')['alarmHistory']['pending']>0,'capture startup failures enter existing alarm history')
        finally:
            if process and process.poll() is None:process.terminate();process.wait(timeout=5)
    print('All',len(checks),'send monitor checks passed using real Npcap and loopback-only UDP.')


if __name__=='__main__':main()
