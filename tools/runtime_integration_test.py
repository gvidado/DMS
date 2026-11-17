"""Real UDP anomaly ring and original-timestamp pcapng replay, using an isolated server."""
import copy
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
import urllib.request
from pathlib import Path
from integration_test import Client, ROOT, free_port, packet, seal, wait, load_replay, seed_send_monitor
from decoder_integration_test import upload
from capture_integration_test import section, block, epb, network


def main():
    checks=[]
    def check(ok,label):
        assert ok,label
        checks.append(label);print('PASS',label,flush=True)
    with tempfile.TemporaryDirectory(prefix='telemetry-runtime-') as scratch:
        root=Path(scratch);shutil.copytree(ROOT/'config',root/'config'); seed_send_monitor(root);shutil.copytree(ROOT/'web',root/'web');(root/'config/decoder.json').unlink(missing_ok=True)
        port=free_port();process=subprocess.Popen([str(ROOT/'TelemetryMonitor.exe'),'--local','--root',str(root),'--web-port',str(port)],stdout=subprocess.PIPE,stderr=subprocess.STDOUT)
        try:
            c=Client(port)
            def ready():
                try:return c.call('/api/auth/me')['setupRequired']
                except OSError:return False
            wait(ready,'runtime server')
            password=secrets.token_urlsafe(18);c.call('/api/auth/setup',dict(username='runtime_qa',name='回放测试',password=password));c.csrf=c.call('/api/auth/login',dict(username='runtime_qa',password=password))['csrf']
            cfg=c.call('/api/decoders')['configuration'];cfg['Catalog']['Parameters']=[p for p in cfg['Catalog']['Parameters'] if p['Group']=='工仪全程' and p['Code'] in ('A1','A4')]
            cfg['Packets'][0].update(SppOffset=-1,ExpectedDatagramLength=0,ExpectedLengthField=1017,MappingConfirmed=True,Groups=['工仪全程'])
            preview=upload(c,'runtime.json',json.dumps(cfg).encode());c.call('/api/decoders/apply',dict(token=preview['token'],revision=preview['revision']))
            c.call('/api/selection',dict(ids=['legacy::工仪全程/A1','legacy::工仪全程/A4']))
            c.call('/api/control',{'action':'demo'},expected=400)
            check(True,'demo requires a pcapng source and never silently generates synthetic values')
            udp_port=free_port(socket.SOCK_DGRAM);settings=c.call('/api/config');settings.update(UdpPort=udp_port,BindAddress='127.0.0.1',SourceIPs=['127.0.0.1']);c.call('/api/config',settings)
            c.call('/api/control',{'action':'start'})
            bad=[]
            with socket.socket(socket.AF_INET,socket.SOCK_DGRAM) as sender:
                for i in range(205):
                    data=packet(i+1);data[-1]^=1;bad.append(bytes(data));sender.sendto(data,('127.0.0.1',udp_port));time.sleep(.001)
            wait(lambda:c.call('/api/state')['received']==205,'205 invalid live datagrams')
            records=c.call('/api/live-abnormal/list')['records']
            check(len(records)==200 and records[0]['id']==205 and records[-1]['id']==6,'live anomaly ring retains exactly the newest 200 records in newest-first order')
            detail=c.call('/api/live-abnormal/detail?id=6')
            check(bytes.fromhex(detail['sppHex'])==bad[5][6:] and bytes.fromhex(detail['udpHex'])==bad[5] and detail['record']['mode']=='实时UDP','live anomaly detail preserves every original SPP byte, UDP prefix and source mode')
            c.call('/api/live-abnormal/detail?id=1',expected=400)
            with c.opener.open(c.base+'/api/live-abnormal/export') as response:raw=response.read()
            rows=list(csv.DictReader(io.StringIO(raw.decode('utf-8-sig'))))
            check(raw.startswith(b'\xef\xbb\xbf') and len(rows)==200 and rows[0]['记录号']=='6' and rows[-1]['记录号']=='205' and bytes.fromhex(rows[-1]['SPP完整内容HEX'])==bad[-1][6:],'live CSV exports the complete rolling window with checksum bytes and no truncated payload')
            c.call('/api/control',{'action':'stop'});check(c.call('/api/live-abnormal/list')['count']==200,'stopping UDP keeps the latest anomaly records')
            c.call('/api/live-abnormal/clear',{},csrf=False,expected=403);Client(port).call('/api/live-abnormal/list',expected=401)
            check(True,'live anomaly reads and CSVs require login and clearing requires CSRF')
            c.call('/api/live-abnormal/clear',{});c.call('/api/control',{'action':'start'})
            nan=packet(1);struct.pack_into('>f',nan,35,float('nan'));seal(nan)
            with socket.socket(socket.AF_INET,socket.SOCK_DGRAM) as sender:sender.sendto(nan,('127.0.0.1',udp_port))
            wait(lambda:c.call('/api/live-abnormal/list')['count']==1,'parameter anomaly')
            check('A4' in c.call('/api/live-abnormal/list')['records'][0]['reason'],'a valid-SPP packet with a parameter decoding error is also retained')
            c.call('/api/control',{'action':'stop'});c.call('/api/live-abnormal/clear',{})
            sequence_window=[100,101,102,103,104,100,105,101,101]
            window_packets=[packet(n) for n in sequence_window]
            c.call('/api/control',{'action':'start'})
            with socket.socket(socket.AF_INET,socket.SOCK_DGRAM) as sender:
                for data in window_packets:sender.sendto(data,('127.0.0.1',udp_port));time.sleep(.002)
            wait(lambda:c.call('/api/state')['received']==len(sequence_window),'recent-five live window')
            live_window=c.call('/api/state')
            check(live_window['accepted']==6 and live_window['duplicates']==2 and live_window['outOfOrder']==1,'live duplicate detection uses the last five received valid sequences, including duplicate receptions')
            c.call('/api/control',{'action':'stop'})
            window_capture=section()+b''.join(epb(network(d,source='127.0.0.1'),ticks=1_700_000_000_000000+i*1000) for i,d in enumerate(window_packets))
            request=urllib.request.Request(c.base+'/api/captures/start?filename=window.pcapng',data=window_capture,headers={'Content-Type':'application/octet-stream','X-CSRF-Token':c.csrf})
            window_job=json.loads(c.opener.open(request).read())['id'];wait(lambda:c.call('/api/captures/status?id='+window_job)['state']=='completed','recent-five capture window')
            window_report=c.call('/api/captures/status?id='+window_job)['summary']
            check(window_report['valid']==6 and window_report['duplicates']==2 and window_report['outOfOrder']==1,'file verification uses the same five-reception duplicate window')
            c.call('/api/captures/delete?id='+window_job,{})
            load_replay(c,window_capture);c.call('/api/replay/speed',{'speed':5});c.call('/api/control',{'action':'demo'})
            wait(lambda:c.call('/api/state')['replay']['state']=='completed','recent-five replay window')
            window_state=c.call('/api/state')
            check(window_state['accepted']==6 and window_state['duplicates']==2 and window_state['outOfOrder']==1,'accelerated file replay also applies the same five-reception duplicate window')
            c.call('/api/replay/speed',{'speed':1})
            values=[]
            for i in range(3):
                data=packet(i+1);data[22:27]=((i+1)*10000).to_bytes(5,'big');seal(data);values.append(data)
            capture=section()+b''.join(epb(network(d),ticks=1_700_000_000_000000+t) for d,t in zip(values,(0,600000,1300000)))
            load_replay(c,capture);before=c.call('/api/decoders');c.call('/api/control',{'action':'demo'});start=time.monotonic()
            wait(lambda:c.call('/api/state')['accepted']==1,'first replay frame')
            time.sleep(.2);s=c.call('/api/state');check(s['accepted']==1 and s['rows'][0]['value']=='1','first frame is decoded immediately and later values are not shown ahead of capture time')
            c.call('/api/control',{'action':'start'},expected=409);c.call('/api/replay/clear',{},expected=409)
            wait(lambda:c.call('/api/state')['accepted']>=2,'second replay frame');elapsed2=time.monotonic()-start
            check(.48<=elapsed2<1.15 and c.call('/api/state')['rows'][0]['value']=='2','600ms timestamp difference is replayed at 1x and parameter values follow the file')
            wait(lambda:c.call('/api/state')['replay']['state']=='completed','replay completion');elapsed3=time.monotonic()-start;s=c.call('/api/state')
            check(1.18<=elapsed3<2.3 and s['accepted']==3 and s['rows'][0]['value']=='3' and s['replay']['captureElapsedMs']==1300,'replay preserves the 1300ms file duration, completes once and retains the last value')
            check(c.call('/api/decoders')==before and s['mode']=='stopped','replay leaves mappings unchanged and releases the receiver mode on completion')
            for speed in (2,3,4,5):
                c.call('/api/replay/speed',{'speed':speed});begin=time.monotonic();c.call('/api/control',{'action':'demo'})
                wait(lambda:c.call('/api/state')['replay']['state']=='completed','accelerated replay')
                elapsed=time.monotonic()-begin;s=c.call('/api/state')
                check(1.3/speed-.08<=elapsed<1.3/speed+.4 and s['accepted']==3 and s['rows'][0]['value']=='3' and s['replay']['speed']==speed,
                      str(speed)+'x replay compresses the timestamp interval without losing packet values or order')
            for invalid in (0,6,1.5,'bad'):
                c.call('/api/replay/speed',{'speed':invalid},expected=400)
            c.call('/api/replay/speed',{'speed':2},csrf=False,expected=403)
            check(c.call('/api/replay/source')['speed']==5,'speed changes validate 1-5 integer range and CSRF without changing the prior value on errors')
            dynamic=section()+b''.join(epb(network(d),ticks=1_700_000_000_000000+t) for d,t in zip(values,(0,1000000,2000000)))
            c.call('/api/replay/speed',{'speed':1});load_replay(c,dynamic);c.call('/api/control',{'action':'demo'});begin=time.monotonic()
            wait(lambda:c.call('/api/state')['accepted']==1,'dynamic speed first frame');time.sleep(.2)
            before_position=c.call('/api/replay/source')['positionMs'];changed=c.call('/api/replay/speed',{'speed':5})
            check(before_position<=changed['positionMs']<before_position+120 and c.call('/api/state')['accepted']==1,'changing speed during playback keeps the virtual position continuous instead of jumping ahead')
            wait(lambda:c.call('/api/state')['replay']['state']=='completed','dynamic speed completion')
            dynamic_elapsed=time.monotonic()-begin
            check(.48<dynamic_elapsed<1.1 and c.call('/api/state')['accepted']==3,'remaining frames use the new speed immediately from the current playback position')
            c.call('/api/replay/speed',{'speed':1});load_replay(c,capture)
            c.call('/api/control',{'action':'demo'});wait(lambda:c.call('/api/state')['accepted']==1,'restart from first frame');c.call('/api/control',{'action':'stop'})
            check(c.call('/api/state')['replay']['state']=='stopped' and c.call('/api/state')['rows'][0]['value']=='1','restart plays from the beginning and stopping retains the current frame')
            long_gap=section()+epb(network(values[0]))+epb(network(values[1]),ticks=1_700_000_000_123456+120_000_000)
            load_replay(c,long_gap);c.call('/api/control',{'action':'demo'});wait(lambda:c.call('/api/state')['accepted']==1,'long-gap first frame');start=time.monotonic();c.call('/api/control',{'action':'stop'})
            check(time.monotonic()-start<.7,'a long original-time gap remains interruptible without waiting for the next frame')
            data=network(values[0]);load_replay(c,section()+block(3,struct.pack('<I',len(data))+data));c.call('/api/control',{'action':'demo'})
            wait(lambda:c.call('/api/state')['replay']['state']=='failed','missing timestamp')
            check('时间戳' in c.call('/api/state')['error'],'missing timestamps fail explicitly instead of inventing replay timing')
            load_replay(c,section()+epb(network(values[0]),ticks=2000000)+epb(network(values[1]),ticks=1000000));c.call('/api/control',{'action':'demo'})
            wait(lambda:c.call('/api/state')['replay']['state']=='failed','backward timestamp');check('倒退' in c.call('/api/state')['error'],'backward timestamps stop replay with a precise error')
            load_replay(c,section()+epb(network(values[0]),ticks=1000000)+section()+epb(network(values[0]),ticks=1200000));c.call('/api/control',{'action':'demo'})
            wait(lambda:c.call('/api/state')['replay']['state']=='completed','section replay')
            check(c.call('/api/state')['accepted']==2 and c.call('/api/state')['duplicates']==0,'pcapng sections use separate sequence epochs while preserving their timestamp interval')
            damaged=bytearray(values[0]);damaged[-1]^=1;load_replay(c,section()+epb(network(damaged)));c.call('/api/control',{'action':'demo'})
            wait(lambda:c.call('/api/state')['replay']['state']=='completed','invalid replay frame')
            rec=c.call('/api/live-abnormal/list')['records'][0]
            check(rec['mode']=='文件演示' and rec['captureFrame']==1 and bytes.fromhex(c.call('/api/live-abnormal/detail?id='+str(rec['id']))['sppHex'])==damaged[6:],'replayed invalid SPP bytes are recorded with their original capture frame number')
            request=urllib.request.Request(c.base+'/api/captures/start?filename=reuse.pcapng',data=capture,headers={'Content-Type':'application/octet-stream','X-CSRF-Token':c.csrf})
            job=json.loads(c.opener.open(request).read())['id'];wait(lambda:c.call('/api/captures/status?id='+job)['state']=='completed','capture for reuse')
            c.call('/api/replay/use-capture?id='+job,{});c.call('/api/captures/delete?id='+job,{})
            c.call('/api/control',{'action':'demo'});wait(lambda:c.call('/api/state')['replay']['state']=='completed','retained source after capture deletion')
            check(c.call('/api/state')['accepted']==3,'an uploaded capture can be reused without another copy and remains readable after clearing its check result')
            c.call('/api/replay/clear',{});check(not c.call('/api/replay/source')['ready'],'clearing the replay source releases its retained file')
            load_replay(c,capture);process.terminate();process.wait(timeout=6)
            check(not list((root/'data/replay').iterdir()) and not list((root/'data/capture-checks').iterdir()),'process exit deletes standalone and reused replay temporary files')
        finally:
            if process.poll() is None:process.terminate();process.wait(timeout=6)
    print('All',len(checks),'runtime integration checks passed.')


if __name__=='__main__':main()
