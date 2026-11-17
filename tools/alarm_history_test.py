"""Persistent complete alarm history, independent confirmation, pagination and CSV."""
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
import sys
from pathlib import Path
from integration_test import Client, ROOT, free_port, packet, seal, wait, load_replay, seed_send_monitor
from decoder_integration_test import upload
from capture_integration_test import section, epb, network


def main():
    checks=[]
    def check(ok,label):
        assert ok,label
        checks.append(label);print('PASS',label,flush=True)
    with tempfile.TemporaryDirectory(prefix='telemetry-alarms-') as scratch:
        root=Path(scratch);shutil.copytree(ROOT/'config',root/'config'); seed_send_monitor(root);shutil.copytree(ROOT/'web',root/'web');(root/'config/decoder.json').unlink(missing_ok=True)
        port=free_port();udp_port=free_port(socket.SOCK_DGRAM)
        def launch():return subprocess.Popen([str(Path(sys.argv[1]).resolve() if len(sys.argv)>1 else ROOT/'TelemetryMonitor.exe'),'--local','--root',str(root),'--web-port',str(port)],stdout=subprocess.PIPE,stderr=subprocess.STDOUT)
        process=launch()
        try:
            c=Client(port)
            def ready():
                try:return c.call('/api/auth/me')['setupRequired']
                except OSError:return False
            wait(ready,'alarm history server');password=secrets.token_urlsafe(18);c.call('/api/auth/setup',dict(username='alarm_admin',name='首位确认人',password=password));c.csrf=c.call('/api/auth/login',dict(username='alarm_admin',password=password))['csrf']
            config=c.call('/api/decoders')['configuration'];config['Catalog']['Parameters']=[p for p in config['Catalog']['Parameters'] if p['Group']=='工仪全程' and p['Code'] in ('A1','A4')]
            config['Packets'][0].update(Name='=SPP,<测试>',SppOffset=-1,ExpectedDatagramLength=0,MappingConfirmed=True)
            preview=upload(c,'alarms.json',json.dumps(config).encode());c.call('/api/decoders/apply',dict(token=preview['token'],revision=preview['revision']))
            cfg=c.call('/api/config');cfg.update(UdpPort=udp_port,BindAddress='127.0.0.1',SourceIPs=['127.0.0.1'],MulticastGroup='',MulticastInterface='',DataTimeoutMs=60000);c.call('/api/config',cfg)
            check(c.call('/api/alarms/list')['total']==0,'ordinary startup and configuration events do not create false alarm records')
            Client(port).call('/api/alarms/list',expected=401);Client(port).call('/api/alarms/export',expected=401);c.call('/api/control',dict(action='ack'),csrf=False,expected=403)
            c.call('/api/alarms/list?page=0',expected=400);c.call('/api/alarms/list?pageSize=101',expected=400);c.call('/api/alarms/list?status=bad',expected=400)
            check(True,'alarm reads and CSV require login, confirmation requires CSRF and pagination is validated')
            c.call('/api/control',dict(action='start'))
            with socket.socket(socket.AF_INET,socket.SOCK_DGRAM) as sender:
                for i in range(450):
                    data=packet(i+1);data[-1]^=1;sender.sendto(data,('127.0.0.1',udp_port));time.sleep(.001)
            wait(lambda:c.call('/api/state')['received']==450,'450 invalid packets')
            first=c.call('/api/alarms/list');anchor=first['throughId']
            check(first['total']==1 and first['pending']==1 and first['pages']==1 and len(first['records'])==1 and c.call('/api/live-abnormal/list')['count']==200,'450 identical checksum warnings create one permanent alarm while the latest 200 raw packets remain available')
            unified=c.call('/api/abnormal-records');check(unified['total']==1 and unified['records'][0]['type']=='spp' and not unified['records'][0]['released'],'live checksum failures appear as a single pending SPP incident in unified history')
            collected=[]
            for page in range(1,2):collected+=c.call('/api/alarms/list?page='+str(page)+'&throughId='+str(anchor))['records']
            check([r['id'] for r in collected]==[1] and all(r['category']=='SPP异常' and r['packet']=='=SPP,<测试>' for r in collected) and collected[0]['sequence']==1,'the first alarm retains its stable ID, original SPP context and first sequence')
            def exported(client):
                with client.opener.open(client.base+'/api/alarms/export') as response:raw=response.read()
                return raw,list(csv.DictReader(io.StringIO(raw.decode('utf-8-sig'))))
            raw,rows=exported(c)
            check(raw.startswith(b'\xef\xbb\xbf') and len(rows)==1 and rows[0]['记录号']=='1' and all(r['SPP包'].startswith("'=") for r in rows),'UTF-8 BOM CSV exports the deduplicated alarm and safely preserves Chinese, commas and formula-like cells')
            c.call('/api/control',dict(action='ack'));confirmed=c.call('/api/alarms/list');first_ack=confirmed['records'][0]['confirmedAt']
            check(confirmed['total']==1 and confirmed['pending']==0 and all(r['confirmed'] and r['confirmedBy']=='首位确认人' and r['confirmedUsername']=='alarm_admin' for r in confirmed['records']),'confirmation retains all records with actor and timestamp even when no valid data has ever arrived')
            check(c.call('/api/alarms/list?status=pending')['matching']==0 and c.call('/api/alarms/list?status=confirmed')['matching']==1,'confirmed and pending filters partition the deduplicated history')
            with socket.socket(socket.AF_INET,socket.SOCK_DGRAM) as sender:
                bad=packet(451);bad[-1]^=1;sender.sendto(bad,('127.0.0.1',udp_port))
            wait(lambda:c.call('/api/state')['received']==451,'same checksum warning from a new source port')
            check(c.call('/api/alarms/list')['total']==1 and c.call('/api/alarms/list')['pending']==0,'confirmation and a changed UDP source port do not re-arm the same warning')
            c.call('/api/live-abnormal/clear',{});c.call('/api/control',dict(action='stop'))
            check(c.call('/api/alarms/list')['total']==1,'clearing temporary SPP bytes and stopping reception do not delete alarm history')
            cfg['DataTimeoutMs']=300;c.call('/api/config',cfg);c.call('/api/control',dict(action='start'))
            wait(lambda:c.call('/api/state')['sources'][0]['alarm'],'offline alarm')
            offline_total=c.call('/api/alarms/list')['total'];check(offline_total==2,'a source timeout creates one structured alarm record')
            c.call('/api/alarms/ack',dict(id=c.call('/api/alarms/list')['records'][0]['id']));time.sleep(.8);state=c.call('/api/state')
            check(not state['sources'][0]['online'] and not state['sources'][0]['alarm'] and state['alarmHistory']['pending']==0 and c.call('/api/alarms/list')['total']==offline_total,'an offline condition can be confirmed without recovery and does not immediately re-trigger on health timer ticks')
            with socket.socket(socket.AF_INET,socket.SOCK_DGRAM) as sender:sender.sendto(packet(1),('127.0.0.1',udp_port))
            wait(lambda:c.call('/api/state')['accepted']==1,'real recovery');time.sleep(.8)
            check(c.call('/api/alarms/list')['total']==2 and not c.call('/api/state')['sources'][0]['alarm'],'recovery followed by another timeout neither records nor re-latches an already confirmed warning')
            with socket.socket(socket.AF_INET,socket.SOCK_DGRAM) as sender:
                bad=packet(2);bad[-1]^=1;sender.sendto(bad,('127.0.0.1',udp_port))
            wait(lambda:c.call('/api/alarms/list')['total']==3,'checksum warning in new listening run');c.call('/api/control',dict(action='stop'))
            second_password=secrets.token_urlsafe(18);c.call('/api/users',dict(username='alarm_operator',name='=第二确认人,测试',password=second_password));second=Client(port);second.csrf=second.call('/api/auth/login',dict(username='alarm_operator',password=second_password))['csrf']
            second.call('/api/control',dict(action='ack'));history=c.call('/api/alarms/list');old=[r for r in history['records'] if r['id']==1]
            check(history['records'][0]['confirmedBy']=='=第二确认人,测试' and all(r['confirmedBy']=='首位确认人' and r['confirmedAt']==first_ack for r in old),'later confirmations record their own actor while preserving the first confirmation of earlier alarms')
            cfg.update(DataTimeoutMs=60000,EnableLossAlarm=True,LossAlarmFrames=2);c.call('/api/config',cfg);c.call('/api/control',dict(action='start'))
            before=c.call('/api/alarms/list')['total']
            with socket.socket(socket.AF_INET,socket.SOCK_DGRAM) as sender:
                for seq in (10,10,8,14,14,13,20,20):sender.sendto(packet(seq),('127.0.0.1',udp_port));time.sleep(.003)
                nan=packet(21);struct.pack_into('>f',nan,35,float('nan'));seal(nan);sender.sendto(nan,('127.0.0.1',udp_port))
            wait(lambda:c.call('/api/state')['received']==9,'counter and parameter anomalies');latest=c.call('/api/alarms/list')['records'][:4]
            check(c.call('/api/alarms/list')['total']==before+4 and {r['category'] for r in latest}=={'重复帧','乱序/旧帧','疑似缺帧','SPP异常'} and any('A4' in r['message'] for r in latest),'duplicate, out-of-order, missing-sequence and parameter decoding abnormalities are all included once')
            loss=next(r for r in latest if r['category']=='疑似缺帧');c.call('/api/alarms/ack',dict(id=loss['id']));state=c.call('/api/state');check(not state['sources'][0]['alarm'] and state['alarmHistory']['pending']==3,'confirming just the loss alarm clears its latch while other anomalies remain pending')
            check(state['duplicates']==3 and state['outOfOrder']==2 and state['lost']==8,'repeated warnings still update every duplicate, old-frame and missing-frame counter')
            with socket.socket(socket.AF_INET,socket.SOCK_DGRAM) as sender:sender.sendto(packet(30),('127.0.0.1',udp_port))
            wait(lambda:c.call('/api/state')['received']==10,'another sequence gap after confirmation')
            check(not c.call('/api/state')['sources'][0]['alarm'] and c.call('/api/alarms/list')['total']==before+4,'changed gap length after confirmation does not add a warning or restore its latch')
            c.call('/api/control',dict(action='stop'));bad=packet(1);bad[-1]^=1
            load_replay(c,section()+epb(network(bad)));c.call('/api/control',dict(action='demo'));wait(lambda:c.call('/api/state')['replay']['state']=='completed','alarm replay')
            newest=c.call('/api/alarms/list')['records'][0]
            check(newest['mode']=='文件演示' and newest['captureFrame']==1 and '校验失败' in newest['message'],'replay alarms retain their file mode and original capture-frame number')
            load_replay(c,section()+epb(network(packet(1)),ticks=2000000)+epb(network(packet(2)),ticks=1000000));c.call('/api/control',dict(action='demo'));wait(lambda:c.call('/api/state')['replay']['state']=='failed','replay timestamp failure')
            check(c.call('/api/alarms/list')['records'][0]['category']=='演示异常','file replay runtime failures enter the same complete alarm history')
            c.call('/api/control',dict(action='ack'))
            check(c.call('/api/state')['error']=='' and c.call('/api/state')['alarmHistory']['pending']==0,'confirmation clears the displayed runtime error without requiring the failed source to be repaired')
            with socket.socket(socket.AF_INET,socket.SOCK_DGRAM) as blocker:
                blocker.setsockopt(socket.SOL_SOCKET,socket.SO_EXCLUSIVEADDRUSE,1);blocker.bind(('127.0.0.1',udp_port));c.call('/api/control',dict(action='start'),expected=409)
            check('启动UDP监听失败' in c.call('/api/alarms/list')['records'][0]['message'],'failed live-listener startup is retained as an operational alarm')
            raw,rows=exported(c);check(len(rows)==c.call('/api/alarms/list')['total'] and any(r['确认人'].startswith("'=") for r in rows) and {'已确认','未确认'}=={r['确认状态'] for r in rows},'full CSV includes both confirmed and new unconfirmed alarms with safely escaped confirmation metadata')
            persisted=c.call('/api/alarms/list');process.terminate();process.wait(timeout=6);journal=root/'data/alarm-history.jsonl'
            check(journal.exists() and journal.stat().st_size>0,'alarm history is an ordinary retained local file after process exit')
            tail=b'{"Kind":"alarm","Id":999'
            with journal.open('ab') as writer:writer.write(tail)
            process=launch();fresh=Client(port)
            def reopened():
                try:return not fresh.call('/api/auth/me')['setupRequired']
                except OSError:return False
            wait(reopened,'alarm history restart');fresh.csrf=fresh.call('/api/auth/login',dict(username='alarm_admin',password=password))['csrf'];reloaded=fresh.call('/api/alarms/list')
            check(reloaded['total']==persisted['total']+1 and reloaded['confirmed']==persisted['confirmed'] and reloaded['records'][0]['category']=='运行告警','restart restores all records and confirmations, and records a recovery note for an incomplete trailing write')
            backups=list((root/'data').glob('alarm-history.jsonl.recovered-*.bak'));check(len(backups)==1 and backups[0].read_bytes().endswith(tail),'trailing-write recovery keeps an exact backup instead of discarding the original history file')
            check(len(exported(fresh)[1])==reloaded['total'],'all history remains exportable after restart and trailing-write recovery')
        finally:
            if process.poll() is None:process.terminate();process.wait(timeout=6)
    print('All',len(checks),'alarm history checks passed.')


if __name__=='__main__':main()
