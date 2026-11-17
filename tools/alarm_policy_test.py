"""Unrecognized SPP is informational; genuine anomalies and old confirmation IDs remain intact."""
import csv
import io
import json
import secrets
import shutil
import socket
import struct
import subprocess
import tempfile
import time
from pathlib import Path
from integration_test import Client, ROOT, free_port, packet, seal, wait, load_replay, seed_send_monitor
from decoder_integration_test import upload
from capture_integration_test import section, epb, network


def main():
    checks=[]
    def check(ok,label):
        assert ok,label
        checks.append(label);print('PASS',label,flush=True)
    with tempfile.TemporaryDirectory(prefix='telemetry-alarm-policy-') as temp:
        root=Path(temp);shutil.copytree(ROOT/'config',root/'config');seed_send_monitor(root);shutil.copytree(ROOT/'web',root/'web');(root/'config/decoder.json').unlink(missing_ok=True)
        (root/'data').mkdir();journal=root/'data/alarm-history.jsonl'
        def alarm(id,message,packet_name='Sheet1',apid=8,level='warn',category='SPP异常'):
            return dict(Kind='alarm',Id=id,Time='2026-10-29 12:00:00',Level=level,Category=category,Message=message,Packet=packet_name,Apid=apid,Sequence=1,Source='127.0.0.1',Mode='实时UDP')
        entries=[alarm(1,'无匹配的SPP包标识，检查APID、偏移、来源IP及包选择','未识别',-1),alarm(2,'Fletcher-16累加和校验失败'),
                 alarm(3,'普通启动信息',level='info',category='运行信息'),alarm(4,'SPP字段未匹配；旧定义：源地址不符','未匹配SPP'),
                 dict(Kind='ack',ThroughId=4,Time='2026-10-29 12:01:00',Username='old_user',Actor='旧确认人'),
                 alarm(5,'设备无有效数据超时',level='error',category='设备超时'),alarm(6,'SPP字段未匹配；旧定义：SPP副导头不完整','未匹配SPP'),
                 alarm(7,'UDP长度不符：收到7，配置为1030','未识别',-1),alarm(8,'Fletcher-16累加和校验失败','未识别',8)]
        original=('\n'.join(json.dumps(e,ensure_ascii=False) for e in entries)+'\n').encode();journal.write_bytes(original)
        port=free_port();udp=free_port(socket.SOCK_DGRAM);c=Client(port);process=None
        def launch():return subprocess.Popen([str(ROOT/'TelemetryMonitor.exe'),'--local','--root',str(root),'--web-port',str(port)],stdout=subprocess.PIPE,stderr=subprocess.STDOUT)
        def ready():
            try:return c.call('/api/auth/me')
            except OSError:return False
        def csv_rows():
            with c.opener.open(c.base+'/api/alarms/export') as response:return list(csv.DictReader(io.StringIO(response.read().decode('utf-8-sig'))))
        def send_all(payloads):
            with socket.socket(socket.AF_INET,socket.SOCK_DGRAM) as sender:
                for data in payloads:sender.sendto(data,('127.0.0.1',udp));time.sleep(.003)
        try:
            process=launch();wait(ready,'alarm policy startup');password=secrets.token_urlsafe(18);c.call('/api/auth/setup',dict(username='policy_admin',name='新确认人',password=password));c.csrf=c.call('/api/auth/login',dict(username='policy_admin',password=password))['csrf']
            page=c.call('/api/alarms/list?pageSize=2')
            check((page['total'],page['pending'],page['confirmed'],page['pages'])==(4,3,1,2) and [r['id'] for r in page['records']]==[8,6],'old unmatched and normal-info records are excluded from alarm counts and pagination')
            check([r['id'] for r in c.call('/api/alarms/list?page=2&pageSize=2')['records']]==[5,2] and [r['id'] for r in c.call('/api/alarms/list?status=confirmed')['records']]==[2],'visible IDs keep their original gaps and old acknowledgement boundaries')
            check([r['id'] for r in c.call('/api/alarms/list?status=pending')['records']]==[8,6,5] and [r['记录号'] for r in csv_rows()]==['2','5','6','8'],'pending filters and full CSV follow the same actual-anomaly policy')
            summary=c.call('/api/state')['alarmHistory'];check((summary['total'],summary['pending'],summary['confirmed'])==(4,3,1) and journal.read_bytes()==original,'live alarm summary filters old records without rewriting or deleting the original history file')
            c.call('/api/control',dict(action='ack'));history=c.call('/api/alarms/list')
            check(history['pending']==0 and history['confirmed']==4 and next(r for r in history['records'] if r['id']==2)['confirmedBy']=='旧确认人' and history['records'][0]['confirmedBy']=='新确认人','confirmation spans original raw IDs and preserves each earlier confirmation actor')
            config=c.call('/api/decoders')['configuration'];config['Catalog']['Parameters']=[p for p in config['Catalog']['Parameters'] if p['Group']=='工仪全程' and p['Code'] in ('A1','A4')];config['Packets'][0].update(Name='Sheet1',ExpectedDatagramLength=0,MappingConfirmed=True)
            preview=upload(c,'policy.json',json.dumps(config).encode());c.call('/api/decoders/apply',dict(token=preview['token'],revision=preview['revision']))
            settings=c.call('/api/config');settings.update(UdpPort=udp,BindAddress='127.0.0.1',SourceIPs=['127.0.0.1'],MulticastGroup='',MulticastInterface='',DataTimeoutMs=60000,MappingConfirmed=True);c.call('/api/config',settings);c.call('/api/control',dict(action='start'))
            source=packet(1);source[19]^=1;seal(source);service=packet(1);service[13]^=1;seal(service);subtype=packet(1);subtype[14]^=1;seal(subtype);unknown_bad=packet(1,77);unknown_bad[-1]^=1
            payloads=[packet(1,77),b'not spp',source,service,subtype,unknown_bad,packet(1,9)];saved=journal.read_bytes();send_all(payloads);wait(lambda:c.call('/api/state')['received']==len(payloads),'unrecognized live data')
            check(c.call('/api/alarms/list')['total']==4 and c.call('/api/state')['alarmHistory']['pending']==0 and journal.read_bytes()==saved,'unknown APIDs, non-SPP and unmatched routing fields do not append warnings or trigger pending alarms')
            records=c.call('/api/live-abnormal/list')['records'];check(len(records)==len(payloads) and all(r['alarm'] is False for r in records),'unrecognized bytes remain available as informational diagnostic records')
            check(all(e['Level']=='info' for e in c.call('/api/state')['events']) and '信息' in c.call('/api/packet')['rawInfo']['result'],'unrecognized runtime messages and the latest-packet description are informational')
            checksum=packet(2);checksum[-1]^=1;length=packet(3);struct.pack_into('>H',length,10,1016);short=packet(1)[:8];nan=packet(4);struct.pack_into('>f',nan,35,float('nan'));seal(nan)
            send_all([checksum,length,short,nan]);wait(lambda:c.call('/api/state')['received']==len(payloads)+4,'actual malformed packets')
            alarms=c.call('/api/alarms/list');check(alarms['total']==8 and alarms['pending']==4 and all(r['category']=='SPP异常' for r in alarms['records'][:4]),'recognized checksum, length, incomplete secondary header and decoding failures still create real alarms')
            check(all(r['alarm'] for r in c.call('/api/live-abnormal/list')['records'][:4]) and len(csv_rows())==8,'raw diagnostics and alarm CSV correctly distinguish genuine failures from unmatched information')
            c.call('/api/control',dict(action='stop'));load_replay(c,section()+epb(network(packet(1,77)))+epb(network(b'not spp'))+epb(network(checksum)));c.call('/api/control',dict(action='demo'));wait(lambda:c.call('/api/state')['replay']['state']=='completed','policy replay')
            check(c.call('/api/alarms/list')['total']==9 and c.call('/api/alarms/list')['records'][0]['captureFrame']==3,'file preview applies the same rule: two unrecognized frames stay informational and the checksum failure alarms')
            c.call('/api/control',dict(action='ack'));process.terminate();process.wait(timeout=5);(root/'config/decoder.json').unlink();process=launch();wait(ready,'legacy policy restart');c.csrf=c.call('/api/auth/login',dict(username='policy_admin',password=password))['csrf']
            before=c.call('/api/alarms/list')['total'];c.call('/api/control',dict(action='start'));send_all([packet(1,77),b'not spp',source,checksum]);wait(lambda:c.call('/api/state')['received']==4,'legacy unknown packets')
            check(c.call('/api/alarms/list')['total']==before+1 and len([r for r in c.call('/api/live-abnormal/list')['records'] if r['alarm']])==1,'legacy decoding also treats unmatched packets as information and retains recognized corruption alarms')
            c.call('/api/control',dict(action='stop'));check(c.call('/api/alarms/list')['confirmed']==9,'filtered history and acknowledgement counts survive process restart')
        finally:
            if process and process.poll() is None:process.terminate();process.wait(timeout=5)
    print('All',len(checks),'alarm policy checks passed.')


if __name__=='__main__':main()
