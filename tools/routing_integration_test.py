"""Check composite SPP routing through manual decode, live UDP, capture and replay."""
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
from capture_integration_test import section, epb, network


def main():
    checks=[]
    def check(ok,label):
        assert ok,label
        checks.append(label);print('PASS',label,flush=True)
    with tempfile.TemporaryDirectory(prefix='telemetry-routing-') as scratch:
        root=Path(scratch);shutil.copytree(ROOT/'config',root/'config'); seed_send_monitor(root);shutil.copytree(ROOT/'web',root/'web');(root/'config/decoder.json').unlink(missing_ok=True)
        port=free_port();process=subprocess.Popen([str(ROOT/'TelemetryMonitor.exe'),'--local','--root',str(root),'--web-port',str(port)],stdout=subprocess.PIPE,stderr=subprocess.STDOUT)
        try:
            c=Client(port)
            def ready():
                try:return c.call('/api/auth/me')['setupRequired']
                except OSError:return False
            wait(ready,'composite routing server')
            password=secrets.token_urlsafe(18);c.call('/api/auth/setup',dict(username='route_qa',name='组合匹配测试',password=password));c.csrf=c.call('/api/auth/login',dict(username='route_qa',password=password))['csrf']
            cfg=c.call('/api/decoders')['configuration'];cfg['Catalog']['Parameters']=[p for p in cfg['Catalog']['Parameters'] if p['Code']=='A1' and p['Group']=='工仪全程']
            base=cfg['Packets'][0];base.update(Id='base',Name='基准包',Apid=8,SppOffset=-1,ExpectedDatagramLength=0,ExpectedLengthField=1017,SourceIP='127.0.0.1',SourceAddress=0x1004,ServiceType=3,ServiceSubtype=25,MappingConfirmed=True,Groups=['工仪全程'])
            definitions=[base]
            for id,name,field,value in (('address','不同源地址','SourceAddress',0x1005),('service','不同业务类型','ServiceType',4),('subtype','不同业务子类型','ServiceSubtype',26)):
                d=copy.deepcopy(base);d.update(Id=id,Name=name);d[field]=value;definitions.append(d)
            cfg['Packets']=definitions;preview=upload(c,'routes.json',json.dumps(cfg).encode());c.call('/api/decoders/apply',dict(token=preview['token'],revision=preview['revision']))
            check(len(c.call('/api/decoders')['configuration']['Packets'])==4,'same APID and source IP may use different configured source address, service or subtype')
            current=c.call('/api/decoders');collision=copy.deepcopy(base);collision['Id']='collision'
            rejected=c.call('/api/decoders/packet',dict(revision=current['revision'],packet=collision),expected=400)
            check('路由冲突' in rejected['error'],'an overlapping definition with the entire same routing tuple is rejected')
            def build(d,value,seq=1):
                data=packet(seq);data[13]=d['ServiceType'];data[14]=d['ServiceSubtype'];struct.pack_into('>H',data,18,d['SourceAddress']);data[22:27]=(value*10000).to_bytes(5,'big');return seal(data)
            good=[build(d,i+1) for i,d in enumerate(definitions)]
            for d,data in zip(definitions,good):
                trial=c.call('/api/decoders/try',dict(packetId=d['Id'],hex=data.hex()))
                assert trial['packet']['id']==d['Id'] and trial['packet']['checksum']=='通过'
            check(True,'all four configured routing tuples decode with their selected packet definitions')
            bad=[]
            for offset,field in ((13,'业务类型'),(14,'业务子类型'),(18,'源地址'),(19,'源地址')):
                data=bytearray(good[0]);data[offset]^=0x10;seal(data);bad.append(data)
                result=c.call('/api/decoders/try',dict(packetId='base',hex=data.hex()),expected=400)
                assert field in result['error'] and '收到0x' in result['error'] and '配置0x' in result['error'],result
            check(True,'each required field mismatch reports received and expected values despite a valid Fletcher checksum')
            mixed=copy.deepcopy(base);mixed.update(SourceAddress=0x1005,ServiceType=4,ServiceSubtype=26)
            combined=build(mixed,9);bad.append(combined)
            for id in ('base','address','service','subtype'):c.call('/api/decoders/try',dict(packetId=id,hex=combined.hex()),expected=400)
            check(True,'partial matches from different definitions cannot be combined into a false successful match')
            informational=bytearray(good[0])
            for offset in (12,15,16,17,20,21):informational[offset]^=0x7f
            seal(informational);check(c.call('/api/decoders/try',dict(packetId='base',hex=informational.hex()))['packet']['checksum']=='通过','PUS flags, counters and destination fields remain informational')
            c.call('/api/selection',dict(ids=[d['Id']+'::工仪全程/A1' for d in definitions]))
            udp_port=free_port(socket.SOCK_DGRAM);settings=c.call('/api/config');settings.update(UdpPort=udp_port,BindAddress='127.0.0.1',SourceIPs=['127.0.0.1']);c.call('/api/config',settings);c.call('/api/control',{'action':'start'})
            crc_bad=bytearray(good[0]);crc_bad[-1]^=1
            payloads=good+bad+[crc_bad]
            with socket.socket(socket.AF_INET,socket.SOCK_DGRAM) as sender:
                for data in payloads:sender.sendto(data,('127.0.0.1',udp_port));time.sleep(.003)
            wait(lambda:c.call('/api/state')['received']==len(payloads),'live composite tuples')
            live=c.call('/api/state');values={r['id']:r['value'] for r in live['rows']}
            check(live['accepted']==4 and live['duplicates']==0 and live['invalid']==len(bad)+1 and all(values[d['Id']+'::工仪全程/A1']==str(i+1) for i,d in enumerate(definitions)),
                  'live UDP routes same-sequence same-APID packets separately and mismatches never overwrite any parameter')
            anomaly=c.call('/api/live-abnormal/list')['records'];check(len(anomaly)==len(bad)+1 and any('SPP字段未匹配' in r['reason'] and r['packet']=='未匹配SPP' for r in anomaly),'live matching failures are retained as unassigned abnormal packets with detailed reasons')
            c.call('/api/control',{'action':'stop'})
            capture=section()+b''.join(epb(network(data,source='127.0.0.1'),ticks=1_700_000_000_000000+i*10000) for i,data in enumerate(payloads))
            request=urllib.request.Request(c.base+'/api/captures/start?filename=routes.pcapng',data=capture,headers={'Content-Type':'application/octet-stream','X-CSRF-Token':c.csrf})
            job=json.loads(c.opener.open(request).read())['id'];wait(lambda:c.call('/api/captures/status?id='+job)['state']=='completed','capture composite tuples')
            report=c.call('/api/captures/status?id='+job+'&full=true');s=report['summary']
            check(s['valid']==4 and s['unmatched']==len(bad) and s['invalid']==1 and s['duplicates']==0 and all(p['Valid']==1 for p in report['packets']),
                  'capture verification uses the same composite key and independent sequence counters')
            with c.opener.open(c.base+'/api/captures/export?id='+job+'&format=errors') as response:rows=list(csv.DictReader(io.StringIO(response.read().decode('utf-8-sig'))))
            check(len(rows)==len(bad)+1 and sum(r['状态']=='未匹配' for r in rows)==len(bad) and all(bytes.fromhex(row['SPP完整内容HEX'])==data[6:] for row,data in zip(rows,bad+[crc_bad])),
                  'secondary routing mismatches are included in abnormal CSV with their complete original SPP bytes')
            c.call('/api/live-abnormal/clear',{});load_replay(c,capture);before=c.call('/api/decoders');c.call('/api/control',{'action':'demo'})
            wait(lambda:c.call('/api/state')['replay']['state']=='completed','replay composite tuples')
            replay=c.call('/api/state');check(replay['accepted']==4 and replay['invalid']==len(bad)+1 and replay['duplicates']==0 and c.call('/api/decoders')==before,
                  '1x replay uses identical matching and preserves all saved definitions')
        finally:
            process.terminate();process.wait(timeout=6)
    print('All',len(checks),'routing integration checks passed.')


if __name__=='__main__':main()
