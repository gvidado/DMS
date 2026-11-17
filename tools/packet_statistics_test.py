"""Per-definition live SPP counts, duplicate windows, stop/reset and replay isolation."""
import copy
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
from integration_test import Client, ROOT, free_port, packet, seal, wait, load_replay, seed_send_monitor
from decoder_integration_test import upload
from capture_integration_test import section, epb, network


def main():
    checks=[]
    def check(ok,label):
        assert ok,label
        checks.append(label);print('PASS',label,flush=True)
    def counts(state):return {p['id']:p for p in state['packetStatistics']}
    with tempfile.TemporaryDirectory(prefix='telemetry-packet-stats-') as scratch:
        root=Path(scratch);shutil.copytree(ROOT/'config',root/'config'); seed_send_monitor(root);shutil.copytree(ROOT/'web',root/'web');(root/'config/decoder.json').unlink(missing_ok=True)
        port=free_port();udp_port=free_port(socket.SOCK_DGRAM)
        process=subprocess.Popen([str(ROOT/'TelemetryMonitor.exe'),'--local','--root',str(root),'--web-port',str(port)],stdout=subprocess.PIPE,stderr=subprocess.STDOUT)
        try:
            c=Client(port)
            def ready():
                try:return c.call('/api/auth/me')['setupRequired']
                except OSError:return False
            wait(ready,'packet statistics server');password=secrets.token_urlsafe(18);c.call('/api/auth/setup',dict(username='stats_qa',name='分包统计测试',password=password));c.csrf=c.call('/api/auth/login',dict(username='stats_qa',password=password))['csrf']
            config=c.call('/api/decoders')['configuration'];config['Catalog']['Parameters']=[p for p in config['Catalog']['Parameters'] if p['Code']=='A1' and p['Group']=='工仪全程']
            a=config['Packets'][0];a.update(Id='a',Name='SPP-A',Apid=8,Enabled=True,MappingConfirmed=True,SppOffset=-1,ExpectedDatagramLength=0,ExpectedLengthField=1017,Groups=['工仪全程'])
            b=copy.deepcopy(a);b.update(Id='b',Name='SPP-B',SourceAddress=0x1005)
            pending=copy.deepcopy(a);pending.update(Id='pending',Name='待确认包',Apid=9,MappingConfirmed=False)
            disabled=copy.deepcopy(a);disabled.update(Id='disabled',Name='停用包',Apid=10,Enabled=False)
            config['Packets']=[a,b,pending,disabled];preview=upload(c,'statistics.json',json.dumps(config).encode());c.call('/api/decoders/apply',dict(token=preview['token'],revision=preview['revision']))
            c.call('/api/selection',dict(ids=['a::工仪全程/A1','b::工仪全程/A1','pending::工仪全程/A1']))
            settings=c.call('/api/config');settings.update(UdpPort=udp_port,BindAddress='127.0.0.1',SourceIPs=['127.0.0.1'],MulticastGroup='',MulticastInterface='');c.call('/api/config',settings)
            initial=c.call('/api/state');initial_rows=counts(initial)
            check(set(initial_rows)=={'a','b','pending'} and all(p['received']==0 and p['duplicates']==0 for p in initial_rows.values()),'all enabled definitions appear with zero counters before reception; disabled definitions are omitted')
            check(initial_rows['a']['decodingEnabled'] and not initial_rows['pending']['decodingEnabled'] and initial_rows['pending']['status']=='映射待确认','mapping confirmation is visible independently of receive counters')
            def build(definition,seq):
                data=packet(seq,definition['Apid']);struct.pack_into('>H',data,18,definition['SourceAddress']);data[13]=definition['ServiceType'];data[14]=definition['ServiceSubtype'];return seal(data)
            a_packets=[build(a,n) for n in (100,101,100,102,103,104,105,106,100,100)]
            crc=bytearray(build(a,107));crc[-1]^=1
            length=build(a,107);struct.pack_into('>H',length,10,1016);seal(length)
            unmatched=build(a,108);unmatched[13]=5;seal(unmatched)
            payloads=a_packets+[crc,crc,length]+[build(b,100),build(b,100),build(b,101)]+[build(pending,1),build(pending,1),unmatched,build(disabled,1)]
            c.call('/api/control',dict(action='start'))
            with socket.socket(socket.AF_INET,socket.SOCK_DGRAM) as sender:
                for data in payloads:sender.sendto(data,('127.0.0.1',udp_port));time.sleep(.002)
            wait(lambda:c.call('/api/state')['received']==len(payloads),'per-packet live counting')
            state=c.call('/api/state');rows=counts(state)
            check(state['packetStatisticsMode']=='live' and state['packetStatisticsStartedAt'] and rows['a']['received']==13 and rows['b']['received']==3 and rows['pending']['received']==2,'each matched SPP definition has its own receive total, including same-APID definitions')
            check(rows['a']['duplicates']==2 and rows['b']['duplicates']==1 and rows['pending']['duplicates']==1 and state['duplicates']==4,'duplicate counts are attributed to the correct definition with the recent-five sequence rule')
            check(rows['a']['invalid']==3 and rows['a']['outOfOrder']==1 and rows['a']['accepted']==7,'identified checksum and length failures count as received while duplicate and late frames retain separate dispositions')
            check(sum(p['received'] for p in rows.values())==18 and state['received']==20 and state['invalid']==5,'unmatched tuples and disabled packets do not pollute enabled-definition totals')
            check(all(p['received']==p['accepted']+p['duplicates']+p['invalid']+p['outOfOrder'] for p in rows.values()),'per-definition counter components reconcile to their receive total')
            values={r['id']:r['value'] for r in state['rows']};check(values['pending::工仪全程/A1']=='—' and rows['pending']['received']==2,'an enabled but unconfirmed definition still counts reception without updating real parameter values')
            second=Client(port);second.csrf=second.call('/api/auth/login',dict(username='stats_qa',password=password))['csrf']
            check(second.call('/api/state')['packetStatistics']==state['packetStatistics'],'all authenticated browsers see the same server-side packet statistics')
            Client(port).call('/api/state',expected=401)
            capture=section()+epb(network(build(a,1),source='127.0.0.1'))
            request=urllib.request.Request(c.base+'/api/captures/start?filename=isolated.pcapng',data=capture,headers={'Content-Type':'application/octet-stream','X-CSRF-Token':c.csrf})
            job=json.loads(c.opener.open(request).read())['id'];wait(lambda:c.call('/api/captures/status?id='+job)['state']=='completed','independent file checking')
            check(c.call('/api/state')['packetStatistics']==state['packetStatistics'],'pcapng file verification never adds to live per-SPP counters')
            c.call('/api/control',dict(action='stop'));stopped=c.call('/api/state')
            check(stopped['packetStatistics']==state['packetStatistics'] and stopped['packetStatisticsMode']=='live','stopping keeps all per-SPP totals and their receive-session identity')
            c.call('/api/control',dict(action='start'));restarted=c.call('/api/state')
            check(all(p['received']==0 and p['duplicates']==0 for p in restarted['packetStatistics']),'a successful new listener session resets every enabled packet counter')
            c.call('/api/control',dict(action='stop'));settings['SourceIPs']=['192.0.2.99'];c.call('/api/config',settings);c.call('/api/control',dict(action='start'))
            with socket.socket(socket.AF_INET,socket.SOCK_DGRAM) as sender:sender.sendto(build(a,1),('127.0.0.1',udp_port))
            wait(lambda:c.call('/api/state')['filtered']==1,'filtered source')
            check(all(p['received']==0 for p in c.call('/api/state')['packetStatistics']),'a filtered source increments global reception but not any SPP definition counter')
            c.call('/api/control',dict(action='stop'));settings['SourceIPs']=['127.0.0.1'];c.call('/api/config',settings)
            replay_data=section()+b''.join(epb(network(data,source='127.0.0.1'),ticks=1_700_000_000_000000+i*10000) for i,data in enumerate([build(a,1),build(a,1),build(b,1)]))
            load_replay(c,replay_data);c.call('/api/control',dict(action='demo'));wait(lambda:c.call('/api/state')['replay']['state']=='completed','packet statistics replay')
            replay=c.call('/api/state');r=counts(replay)
            check(replay['packetStatisticsMode']=='demo' and r['a']['received']==2 and r['a']['duplicates']==1 and r['b']['received']==1 and r['pending']['received']==0,'file demo starts its own clearly identified per-SPP session and retains the final counts')
            process.terminate();process.wait(timeout=6);process=subprocess.Popen([str(ROOT/'TelemetryMonitor.exe'),'--local','--root',str(root),'--web-port',str(port)],stdout=subprocess.PIPE,stderr=subprocess.STDOUT)
            fresh=Client(port)
            def reopened():
                try:return not fresh.call('/api/auth/me')['setupRequired']
                except OSError:return False
            wait(reopened,'packet statistics restart');fresh.csrf=fresh.call('/api/auth/login',dict(username='stats_qa',password=password))['csrf']
            check(not fresh.call('/api/state')['packetStatisticsMode'] and all(p['received']==0 for p in fresh.call('/api/state')['packetStatistics']),'process restart begins with empty in-memory counters and preserves configured enabled definitions')
        finally:
            if process.poll() is None:process.terminate();process.wait(timeout=6)
    print('All',len(checks),'packet statistics checks passed.')


if __name__=='__main__':main()
