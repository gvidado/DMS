"""Persistent command learning from real Npcap UDP; all network traffic stays on loopback."""
import json
import secrets
import shutil
import socket
import subprocess
import tempfile
import time
import sys
from pathlib import Path
from integration_test import Client, ROOT, free_port, wait, seed_send_monitor


def main():
    executable=Path(sys.argv[1]).resolve() if len(sys.argv)>1 else ROOT/'TelemetryMonitor.exe'
    checks=[]
    def check(ok,label):
        assert ok,label
        checks.append(label);print('PASS',label,flush=True)
    with tempfile.TemporaryDirectory(prefix='telemetry-commands-') as temporary:
        root=Path(temporary);shutil.copytree(ROOT/'config',root/'config');seed_send_monitor(root);shutil.copytree(ROOT/'web',root/'web');(root/'config/decoder.json').unlink(missing_ok=True)
        file=root/'config/observed-commands.json';file.write_text(json.dumps(dict(SchemaVersion=1,SourceIP='127.0.0.1',IdentityRule='destination-ip-and-udp-payload',NextNumber=1,Commands=[])),encoding='utf-8')
        port=free_port();udp=free_port(socket.SOCK_DGRAM);process=None;c=Client(port);password=secrets.token_urlsafe(18)
        def launch():return subprocess.Popen([str(executable),'--local','--root',str(root),'--web-port',str(port)],stdout=subprocess.PIPE,stderr=subprocess.STDOUT)
        def ready():
            try:return c.call('/api/auth/me')
            except OSError:return False
        def login():c.csrf=c.call('/api/auth/login',dict(username='command_qa',password=password))['csrf']
        def stop_process():
            if process and process.poll() is None:process.terminate();process.wait(timeout=6)
        def state():return c.call('/api/send-monitor/state')
        def count():return state()['commands']['count']
        def action(value):return c.call('/api/control',dict(action=value))
        def exported():
            with c.opener.open(c.base+'/api/send-monitor/commands/export',timeout=30) as response:return json.loads(response.read().decode('utf-8-sig'))
        def disk():return json.loads(file.read_text(encoding='utf-8-sig'))
        try:
            process=launch();wait(ready,'command store server');c.call('/api/auth/setup',dict(username='command_qa',name='指令配置测试',password=password));login()
            Client(port).call('/api/send-monitor/commands/export',expected=401)
            check(state()['commands']['ready'] and count()==0 and exported()['Commands']==[],'the persistent command configuration is readable and its download requires login')
            interfaces=c.call('/api/send-monitor/interfaces');adapter=next(a for a in interfaces['interfaces'] if a['loopback'])
            c.call('/api/send-monitor/config',dict(SourceIPs=['127.0.0.1','127.0.0.4'],DestinationIPs=['127.0.0.2','127.0.0.3'],InterfaceId=adapter['id'],DestinationPort=0))
            settings=c.call('/api/config');settings.update(BindAddress='127.0.0.1',UdpPort=udp,SourceIPs=['127.0.0.1'],MulticastGroup='',MulticastInterface='',DataTimeoutMs=60000);c.call('/api/config',settings)
            with socket.socket(socket.AF_INET,socket.SOCK_DGRAM) as receiver, socket.socket(socket.AF_INET,socket.SOCK_DGRAM) as receiver3, socket.socket(socket.AF_INET,socket.SOCK_DGRAM) as alternate, socket.socket(socket.AF_INET,socket.SOCK_DGRAM) as sender, socket.socket(socket.AF_INET,socket.SOCK_DGRAM) as sender2, socket.socket(socket.AF_INET,socket.SOCK_DGRAM) as other:
                receiver.bind(('127.0.0.2',0));receiver3.bind(('127.0.0.3',receiver.getsockname()[1]));alternate.bind(('127.0.0.2',0));sender.bind(('127.0.0.1',0));sender2.bind(('127.0.0.1',0));other.bind(('127.0.0.4',0))
                for item in (receiver,receiver3,alternate):item.settimeout(3)
                def send(payload,target=receiver,origin=sender):origin.sendto(payload,target.getsockname());assert target.recvfrom(65535)[0]==payload
                action('start');a=b'\x00\x00\x01\xff';send(a);wait(lambda:count()==1,'first command')
                first=disk()['Commands'][0];check(first['Name']=='指令1' and first['DestinationIP']=='127.0.0.2' and first['PayloadHex']=='00 00 01 FF' and first['Length']==4,'an unfamiliar packet creates 指令1 with the complete payload, destination and leading zero bytes')
                before=file.read_bytes();send(a);wait(lambda:state()['total']==2,'repeat packet')
                check(count()==1 and file.read_bytes()==before,'an existing command is neither duplicated nor rewritten while the transmission counter still increments')
                send(a,receiver3);wait(lambda:len(disk()['Commands'][0]['DestinationIPs'])==2,'same payload to second IP');check(count()==1 and disk()['Commands'][0]['Name']=='指令1' and disk()['Commands'][0]['DestinationIPs']==['127.0.0.2','127.0.0.3'] and disk()['NextNumber']==2,'same payload to a new destination extends the original command without consuming a number')
                before=file.read_bytes();send(a,receiver,sender2);wait(lambda:state()['total']==4,'different source port');check(count()==1 and file.read_bytes()==before,'source UDP port changes do not duplicate an existing payload command')
                b=b'\x01\x00';send(b);wait(lambda:count()==2,'second payload');before=file.read_bytes();send(b,alternate);wait(lambda:state()['total']==6,'different destination port')
                check(count()==2 and file.read_bytes()==before,'destination UDP port changes do not duplicate a command')
                send(b'from-other-source',receiver,other);wait(lambda:state()['total']==7,'other monitored source');check(count()==2,'other IPs may be monitored and counted but do not enter this source-specific command configuration')
                send(a+b'\x00');send(b'');wait(lambda:count()==4,'changed byte and empty payload');check(disk()['Commands'][2]['Name']=='指令3' and disk()['Commands'][3]['PayloadHex']=='','any changed payload byte produces a new command and empty UDP payloads are preserved')
                for i in range(30):send(b'new-command-'+bytes([i]))
                wait(lambda:count()==34,'34 unique payload commands');all_commands=exported()
                check(len(all_commands['Commands'])==34 and state()['retained']==39 and [r['Name'] for r in all_commands['Commands']]==['指令'+str(i) for i in range(1,35)],'unique payload commands are numbered independently from 39 actual transmissions')
                action('stop');c.call('/api/send-monitor/control',dict(action='clear'));before=file.read_bytes();check(count()==34 and state()['total']==0,'clearing transient transmission counters never clears learned commands')
                action('start');send(a,receiver3);wait(lambda:state()['total']==1,'restarted capture duplicate');check(count()==34 and file.read_bytes()==before,'a repeated secondary destination is neither appended again nor rewritten')
                action('stop');manual=disk();manual['Commands'][0]['Name']='人工命名的指令';file.write_text(json.dumps(manual,ensure_ascii=False),encoding='utf-8');manual_bytes=file.read_bytes();action('start');send(b'future-command')
                wait(lambda:bool(state()['commands']['error']),'external edit protection');check(file.read_bytes()==manual_bytes and count()==34 and state()['total']==1 and c.call('/api/state')['alarmHistory']['pending']>0,'external file edits are preserved and reported instead of being overwritten; capture counting continues')
                action('stop');stop_process();process=launch();wait(ready,'command persistence restart');login();action('start');send(a);wait(lambda:state()['total']==1,'duplicate after process restart')
                check(count()==34 and disk()['Commands'][0]['Name']=='人工命名的指令' and file.read_bytes()==manual_bytes,'process restart reloads every command and preserves an edited name without recording a duplicate')
                send(b'future-command');wait(lambda:count()==35,'next learned command');check(disk()['Commands'][-1]['Name']=='指令35' and disk()['Commands'][0]['Name']=='人工命名的指令','the next unfamiliar command continues numbering and keeps all existing names')
                action('stop');stop_process();damaged=b'{"broken":';file.write_bytes(damaged);process=launch();wait(ready,'malformed command config');login()
                check(not state()['commands']['ready'] and file.read_bytes()==damaged and c.call('/api/state')['alarmHistory']['pending']>0,'a malformed configuration is retained intact and raises an explicit diagnostic instead of being silently replaced')
                stop_process();large=[]
                for i in range(1,51):
                    payload=bytes([i])+b'\x00'*59999;large.append(dict(Number=i,Name='指令'+str(i),DestinationIP='127.0.0.2',PayloadHex=payload.hex(' ').upper(),Length=len(payload),FirstSeenUtc='2026-10-30T00:00:00Z'))
                file.write_text(json.dumps(dict(SchemaVersion=1,SourceIP='127.0.0.1',IdentityRule='destination-ip-and-udp-payload',NextNumber=51,Commands=large),ensure_ascii=False),encoding='utf-8');process=launch();wait(ready,'large command configuration',seconds=30);login()
                check(file.stat().st_size>8*1024*1024 and count()==50,'the complete command configuration can exceed the existing 8MB configuration-import limit')
                action('start');before=file.read_bytes();send(bytes([50])+b'\x00'*59999);wait(lambda:state()['total']==1,'large known payload')
                check(count()==50 and file.read_bytes()==before,'large payloads are compared in full and remain deduplicated after loading the configuration')
                send(b'after-large-config');wait(lambda:count()==51,'append to large configuration',seconds=20);download=exported()
                check(len(download['Commands'])==51 and download['Commands'][-1]['Name']=='指令51' and bytes.fromhex(download['Commands'][49]['PayloadHex'])==bytes([50])+b'\x00'*59999,'large JSON download and atomic append preserve every original payload and the next command name')
        finally:stop_process()
    print('All',len(checks),'observed-command configuration checks passed.')


if __name__=='__main__':main()
