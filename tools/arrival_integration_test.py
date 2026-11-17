"""Verify immediate UDP processing and timeout independent of reference frame period."""
import json
import secrets
import shutil
import socket
import subprocess
import tempfile
import time
from pathlib import Path
from integration_test import Client, ROOT, free_port, packet, seal, wait, seed_send_monitor


def main():
    checks=[]
    def check(ok,label):
        assert ok,label
        checks.append(label);print('PASS',label,flush=True)
    with tempfile.TemporaryDirectory(prefix='telemetry-arrival-') as scratch:
        folder=Path(scratch);shutil.copytree(ROOT/'config',folder/'config'); seed_send_monitor(folder);shutil.copytree(ROOT/'web',folder/'web');(folder/'config/decoder.json').unlink(missing_ok=True)
        saved=folder/'config/server.json';legacy=json.loads(saved.read_text(encoding='utf-8-sig'));legacy.pop('DataTimeoutMs',None);legacy.update(FramePeriodMs=200,OfflineFrames=5)
        saved.write_text(json.dumps(legacy),encoding='utf-8');old_bytes=saved.read_bytes()
        port=free_port();udp_port=free_port(socket.SOCK_DGRAM)
        process=subprocess.Popen([str(ROOT/'TelemetryMonitor.exe'),'--local','--root',str(folder),'--web-port',str(port)],stdout=subprocess.PIPE,stderr=subprocess.STDOUT)
        try:
            c=Client(port)
            def ready():
                try:return c.call('/api/auth/me')['setupRequired']
                except OSError:return False
            wait(ready,'arrival server');password=secrets.token_urlsafe(18);c.call('/api/auth/setup',dict(username='arrival_qa',name='到包接收测试',password=password));c.csrf=c.call('/api/auth/login',dict(username='arrival_qa',password=password))['csrf']
            migrated=json.loads(saved.read_text(encoding='utf-8-sig'))
            check(migrated['DataTimeoutMs']==1000 and saved.with_suffix('.json.bak').read_bytes()==old_bytes,'old reference-period times offline-count migrates once into an explicit timeout with an exact backup')
            c.call('/api/selection',dict(ids=['工仪全程/A1']))
            cfg=c.call('/api/config');cfg.update(UdpPort=udp_port,BindAddress='127.0.0.1',SourceIPs=['127.0.0.1'],MulticastGroup='',MulticastInterface='',MappingConfirmed=True,FramePeriodMs=160,DataTimeoutMs=1500);c.call('/api/config',cfg)
            c.call('/api/config',dict(cfg,DataTimeoutMs=-1),expected=400)
            c.call('/api/control',dict(action='start'))
            with socket.socket(socket.AF_INET,socket.SOCK_DGRAM) as sender:
                def send(seq):
                    data=packet(seq);data[22:27]=(seq*10000).to_bytes(5,'big');seal(data);sender.sendto(data,('127.0.0.1',udp_port))
                before=time.monotonic()
                for seq in range(1,13):send(seq);time.sleep(.001)
                wait(lambda:c.call('/api/state')['accepted']==12,'burst UDP reception')
                elapsed=time.monotonic()-before;state=c.call('/api/state')
                check(elapsed<1.2 and state['received']==12 and state['rows'][0]['value']=='12','a twelve-frame millisecond burst is fully decoded without waiting for 160ms receive slots')
                time.sleep(.35);state=c.call('/api/state')
                check(state['rows'][0]['status']=='有效' and state['sources'][0]['online'] and not state['sources'][0]['alarm'],'a gap longer than 160ms retains valid data until the independent data timeout')
                send(13);wait(lambda:c.call('/api/state')['accepted']==13,'irregular subsequent UDP frame')
                check(c.call('/api/state')['rows'][0]['value']=='13','an irregular later arrival immediately replaces the current parameter value')
                wait(lambda:c.call('/api/state')['sources'][0]['alarm'],'independent data timeout')
                state=c.call('/api/state')
                check(state['rows'][0]['status']=='过期' and not state['sources'][0]['online'],'parameter expiry and offline alarm use the explicit 1500ms timeout')
                send(14);wait(lambda:c.call('/api/state')['accepted']==14,'arrival after a long idle gap')
                check(c.call('/api/state')['rows'][0]['status']=='有效' and c.call('/api/state')['rows'][0]['value']=='14','the receiver remains waiting through an offline interval and processes the next datagram normally')
                c.call('/api/control',dict(action='ack'));time.sleep(.05);before=time.monotonic();c.call('/api/control',dict(action='stop'))
                check(time.monotonic()-before<.7,'stopping promptly wakes a blocked receive even with no new datagrams')
                cfg['FramePeriodMs']=60000;c.call('/api/config',cfg)
                check(c.call('/api/config')['DataTimeoutMs']==1500,'changing only the reference period leaves the persisted timeout unchanged')
                cfg['DataTimeoutMs']=450;c.call('/api/config',cfg);c.call('/api/control',dict(action='start'));before=time.monotonic();send(1)
                wait(lambda:c.call('/api/state')['accepted']==1,'arrival with a 60-second reference period')
                check(time.monotonic()-before<.6,'a 60-second reference period does not delay the first UDP frame')
                wait(lambda:c.call('/api/state')['sources'][0]['alarm'],'450ms timeout independent of a 60-second reference period')
                check(c.call('/api/state')['rows'][0]['status']=='过期','short explicit timeout expires data independently of a much longer reference period')
            c.call('/api/control',dict(action='stop'));process.terminate();process.wait(timeout=6)
            process=subprocess.Popen([str(ROOT/'TelemetryMonitor.exe'),'--local','--root',str(folder),'--web-port',str(port)],stdout=subprocess.PIPE,stderr=subprocess.STDOUT)
            fresh=Client(port)
            def reopened():
                try:return not fresh.call('/api/auth/me')['setupRequired']
                except OSError:return False
            wait(reopened,'arrival configuration restart');fresh.csrf=fresh.call('/api/auth/login',dict(username='arrival_qa',password=password))['csrf']
            cfg=fresh.call('/api/config');check(cfg['FramePeriodMs']==60000 and cfg['DataTimeoutMs']==450,'separate reference and timeout settings survive restart without being recalculated')
        finally:
            if process.poll() is None:process.terminate();process.wait(timeout=6)
    print('All',len(checks),'arrival integration checks passed.')


if __name__=='__main__':main()
