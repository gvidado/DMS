"""Build standards-shaped pcapng fixtures and check the isolated HTTP file-verification workflow."""
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
import urllib.error
import urllib.parse
import urllib.request
from pathlib import Path

from integration_test import Client, ROOT, free_port, packet, seal, wait, load_replay, seed_send_monitor
from decoder_integration_test import upload as upload_config


def block(kind, body, endian='<'):
    body += b'\0' * (-len(body) % 4)
    return struct.pack(endian+'II', kind, len(body)+12) + body + struct.pack(endian+'I', len(body)+12)


def section(endian='<', link=1, resolution=6, offset=0):
    shb = block(0x0a0d0d0a, struct.pack(endian+'IHHq', 0x1a2b3c4d, 1, 0, -1), endian)
    options = struct.pack(endian+'HHB',9,1,resolution) + b'\0'*3 + struct.pack(endian+'HHq',14,8,offset) + b'\0'*4
    return shb + block(1, struct.pack(endian+'HHI',link,0,65535)+options, endian)


def network(payload, vlan=False, fragment=0, port=7000, source='192.168.1.111', raw=False):
    udp = struct.pack('>HHHH',6000,port,len(payload)+8,0) + payload
    ip = bytearray(struct.pack('>BBHHHBBH4s4s',0x45,0,len(udp)+20,1,fragment,64,17,0,socket.inet_aton(source),socket.inet_aton('192.168.1.255')))
    words = struct.unpack('>10H',ip); total=sum(words); total=(total&65535)+(total>>16); total=(total&65535)+(total>>16); struct.pack_into('>H',ip,10,(~total)&65535)
    eth = bytes.fromhex('ffffffffffff001122334455') + (bytes.fromhex('810000010800') if vlan else bytes.fromhex('0800'))
    return (b'' if raw else eth) + ip + udp


def epb(data, ticks=1_700_000_000_123456, endian='<', original=None, interface=0):
    return block(6,struct.pack(endian+'IIIII',interface,ticks>>32,ticks&0xffffffff,len(data),len(data) if original is None else original)+data,endian)


def main():
    checks=[]
    def check(ok,name):
        assert ok,name
        checks.append(name); print('PASS',name,flush=True)
    with tempfile.TemporaryDirectory(prefix='telemetry-capture-') as scratch:
        directory=Path(scratch); shutil.copytree(ROOT/'config',directory/'config'); seed_send_monitor(directory); shutil.copytree(ROOT/'web',directory/'web'); (directory/'config/decoder.json').unlink(missing_ok=True)
        port=free_port(); process=subprocess.Popen([str(ROOT/'TelemetryMonitor.exe'),'--local','--root',str(directory),'--web-port',str(port)],stdout=subprocess.PIPE,stderr=subprocess.STDOUT)
        try:
            client,anon=Client(port),Client(port)
            def ready():
                try:return client.call('/api/auth/me')['setupRequired']
                except (OSError,AssertionError):return False
            wait(ready,'capture server startup')
            password=secrets.token_urlsafe(18); client.call('/api/auth/setup',dict(username='qa_capture',name='抓包测试',password=password)); client.csrf=client.call('/api/auth/login',dict(username='qa_capture',password=password))['csrf']
            cfg=client.call('/api/decoders')['configuration']; cfg['Catalog']['Parameters']=[p for p in cfg['Catalog']['Parameters'] if p['Group']=='工仪全程' and p['Code'] in ('A1','A4','A281')]
            first=cfg['Packets'][0]; first.update(Id='first',Name='第一包',Apid=8,SppOffset=6,ExpectedLengthField=1017,ChecksumMode='sum8to16',MappingConfirmed=True,Groups=['工仪全程'])
            second=copy.deepcopy(first); second.update(Id='second',Name='第二包',Apid=9,Enabled=False)
            cfg['Packets']=[first,second]
            preview=upload_config(client,'capture-config.json',json.dumps(cfg,ensure_ascii=False).encode()); client.call('/api/decoders/apply',dict(token=preview['token'],revision=preview['revision']))
            config_before=client.call('/api/decoders')
            def start(data,expected=200,actor=client,csrf=True,filename='实测数据.pcapng',**options):
                query=dict(filename=filename,port='0',includeDisabled='true'); query.update(options)
                headers={'Content-Type':'application/octet-stream','X-CSRF-Token':actor.csrf if csrf else ''}
                if hasattr(data,'fileno'):
                    headers['Content-Length']=str(Path(data.name).stat().st_size)
                    data=iter(lambda source=data:source.read(65536),b'')
                req=urllib.request.Request(actor.base+'/api/captures/start?'+urllib.parse.urlencode(query),data=data,headers=headers)
                try:response=actor.opener.open(req,timeout=90)
                except urllib.error.HTTPError as error:response=error
                payload=json.loads(response.read().decode()); assert response.status==expected,(response.status,payload)
                return payload.get('id')
            def result(id):
                wait(lambda:client.call('/api/captures/status?id='+id)['state'] in ('completed','failed','cancelled'),'capture processing',seconds=90)
                return client.call('/api/captures/status?id='+id+'&full=true')
            def delete(id):client.call('/api/captures/delete?id='+id,{})
            def errors(id):
                with client.opener.open(client.base+'/api/captures/export?id='+id+'&format=errors') as response:
                    raw=response.read(); assert raw.startswith(b'\xef\xbb\xbf')
                    return list(csv.DictReader(io.StringIO(raw.decode('utf-8-sig'))))
            def run(data,**options):
                id=start(data,**options); report=result(id); delete(id); return report
            real_packets=[bytes.fromhex((ROOT/'tools/fixtures'/name).read_text()) for name in ('spp-sheet1-3086.hex','spp-sheet1-3089.hex')]
            real_definition=copy.deepcopy(first); real_definition.update(SppOffset=-1,ExpectedDatagramLength=0,ChecksumMode='unverified',ChecksumStart=16)
            for real,expected in zip(real_packets,('2748','a6da')):
                assert len(real)==1024 and real[-2:].hex()==expected and seal(bytearray(real),start=0)==real
                trial=client.call('/api/decoders/try',dict(packet=real_definition,hex=real.hex()))
                assert trial['packet']['checksum']=='通过'
                damaged_real=bytearray(real); damaged_real[20]^=1
                client.call('/api/decoders/try',dict(packet=real_definition,hex=damaged_real.hex()),expected=400)
            check(True,'both real user packets pass fixed Fletcher-16, while legacy unverified/start fields cannot disable validation of damaged bytes')
            real_capture=run(section()+b''.join(epb(network(bytes.fromhex('A1 B2 C3 D4 E5 F6')+real)) for real in real_packets))
            check(real_capture['summary']['valid']==2 and real_capture['summary']['invalid']==0,
                  'both real Fletcher packets pass file verification and external UDP prefix bytes are excluded')
            malformed=bytearray(packet(4)); malformed[-1]^=1
            wrong_length=bytearray(packet(4)); struct.pack_into('>H',wrong_length,10,1016); seal(wrong_length)
            varied=bytearray(packet(4))
            for offset in (12,15,16,17,20,21):varied[offset]=0xe0+offset-12
            seal(varied)
            frames=[network(packet(1),vlan=True),network(packet(1)),network(packet(3)),network(packet(2)),network(malformed),network(wrong_length),network(packet(1,99)),bytes.fromhex('ffffffffffff0011223344550806')+b'\0'*28,network(packet(5),fragment=0x2000),network(varied),network(packet(1,9)),bytes.fromhex('ffffffffffff00112233445586dd')+b'\x60'+b'\0'*39,network(packet(5),port=9999)]
            capture=section()+b''.join(epb(frame,ticks=1_700_000_000_123456+i*160000) for i,frame in enumerate(frames))
            id=start(capture); report=result(id); s=report['summary']
            check(report['state']=='completed' and s['frames']==13 and s['udp']==10 and s['other']==1 and s['unsupported']==2,'Ethernet/VLAN IPv4 UDP, ARP, IPv6 and fragments are classified without inventing successes')
            check((s['valid'],s['invalid'],s['unmatched'],s['diagnostic'],s['duplicates'],s['outOfOrder'],s['missing'])==(4,2,1,1,1,1,1),'valid, checksum failure, length failure, unmatched APID, disabled packet and sequence cases have separate counts')
            check(client.call('/api/state')['received']==0 and client.call('/api/decoders')==config_before,'file checking does not inject packets into live data or change config')
            records={r['Frame']:r for r in report['records']}
            check('123456' in records[1]['Time'] and records[6]['Status']=='异常' and '长度' in records[6]['Reason'],'UTC timestamp and original Wireshark frame numbers identify exact errors')
            detail=client.call('/api/captures/detail?id='+id+'&frame=10'); by_code={r['code']:r for r in detail['rows']}
            check(detail['error']=='' and by_code['A4']['value']=='-2.5' and detail['hex'].startswith('00 00 00 00 00 00 08 08'),'packet detail re-reads original UDP bytes and decodes fields while accepting varied secondary-header information')
            checksum_detail=client.call('/api/captures/detail?id='+id+'&frame=5')
            check('校验失败' in checksum_detail['error'] and checksum_detail['hex'],'failed packet keeps raw payload for troubleshooting')
            unmatched_detail=client.call('/api/captures/detail?id='+id+'&frame=7')
            check(unmatched_detail['hex'] and unmatched_detail['rows']==[] and unmatched_detail['error'],'unmatched UDP payload remains available for correcting the SPP configuration')
            response=client.opener.open(client.base+'/api/captures/export?id='+id+'&format=csv'); raw=response.read(); table=list(csv.reader(io.StringIO(raw.decode('utf-8-sig'))))
            check(raw.startswith(b'\xef\xbb\xbf') and len(table)==14 and table[6][0]=='6','complete UTF-8 CSV exports every processed frame, including ignored and unsupported frames')
            abnormal=errors(id)
            check(s['abnormalSaved']==2 and [r['帧号'] for r in abnormal]==['5','6'] and
                  bytes.fromhex(abnormal[0]['SPP完整内容HEX'])==malformed[6:] and
                  bytes.fromhex(abnormal[0]['完整UDP载荷HEX'])==malformed and
                  bytes.fromhex(abnormal[1]['SPP完整内容HEX'])==wrong_length[6:] and
                  abnormal[1]['SPP声明字节数']=='1023' and abnormal[1]['实存SPP字节数']=='1024',
                  'abnormal CSV preserves complete original SPP headers, user bytes and checksum, excludes UDP prefix, and retains actual bytes on length mismatch')
            exported=client.call('/api/captures/export?id='+id+'&format=json')
            check(exported['revision']==config_before['revision'] and len(exported['parameters'])==6,'report includes immutable configuration revision and per-parameter statistics')
            start(capture,actor=anon,expected=401); start(capture,csrf=False,expected=403); start(capture,filename='bad.pcap',expected=400)
            client.call('/api/users',dict(username='capture_other',name='其他用户',password=password)); other=Client(port); other.csrf=other.call('/api/auth/login',dict(username='capture_other',password=password))['csrf']
            other.call('/api/captures/status?id='+id,expected=400); other.call('/api/captures/detail?id='+id+'&frame=1',expected=400); other.call('/api/captures/delete?id='+id,{},expected=400)
            other.call('/api/captures/export?id='+id+'&format=errors',expected=400)
            check(True,'uploads require authentication and CSRF; another account cannot read or delete results')
            filtered=run(capture,port='7000',includeDisabled='false')
            check(filtered['summary']['filtered']==1 and filtered['summary']['unmatched']==2,'destination-port filter and enabled-only choice are applied independently')
            whitelist=run(section()+epb(network(packet(1),source='127.0.0.1')),whitelist='true')
            check(whitelist['summary']['filtered']==1,'optional live-source whitelist is explicit, not silently imposed')
            be=run(section('>',link=101,resolution=9,offset=1)+epb(network(packet(1),raw=True),ticks=1_700_000_000_123456789,endian='>'))
            assert be['summary']['valid']==1 and '21.123456' in be['records'][0]['Time'], (be['state'],be['error'],be['summary'],be['records'])
            check(True,'big-endian sections, raw IP, nanosecond resolution and timestamp offset are read')
            multiple=run(section()+epb(network(packet(100)))+section('>',link=101)+epb(network(packet(1),raw=True),endian='>'))
            check(multiple['summary']['valid']==2 and multiple['summary']['missing']==0,'multiple sections reset interface IDs and isolate sequence epochs')
            spb=run(section()+block(3,struct.pack('<I',len(frames[0]))+frames[0]))
            check(spb['summary']['valid']==1 and spb['summary']['noTimestamp']==1,'simple packet blocks are supported and missing timestamps are disclosed')
            old=run(section()+block(2,struct.pack('<HHIIII',0,0,0,123456,len(frames[0]),len(frames[0]))+frames[0]))
            check(old['summary']['valid']==1,'obsolete packet blocks remain readable')
            truncated_id=start(section()+epb(frames[0][:-5],original=len(frames[0]))); badframe=result(truncated_id)
            check(badframe['summary']['invalid']==1 and '截断' in badframe['records'][0]['Reason'],'snap-length truncation is never decoded as complete telemetry')
            truncated_rows=errors(truncated_id)
            check(len(truncated_rows)==1 and truncated_rows[0]['SPP完整内容HEX']=='' and bytes.fromhex(truncated_rows[0]['抓包原始帧HEX'])==frames[0][:-5],
                  'truncated capture keeps every captured frame byte and does not fabricate a complete SPP packet')
            delete(truncated_id)
            damaged=bytearray(capture); damaged[-1]^=1; bad=run(damaged)
            check(bad['state']=='failed' and bad['error'] and bad['summary']['frames']<13,'corrupt block trailers fail with explicitly partial results')
            unknown=run(section(link=999)+epb(frames[0])); check(unknown['summary']['unsupported']==1 and unknown['summary']['valid']==0,'unsupported link types are counted, not guessed')
            auto=client.call('/api/decoders')['configuration']; auto['Packets'][0]['SppOffset']=-1
            preview=upload_config(client,'auto.json',json.dumps(auto).encode()); client.call('/api/decoders/apply',dict(token=preview['token'],revision=preview['revision']))
            found=run(section()+epb(network(wrong_length)))
            check(found['summary']['invalid']==1 and '长度' in found['records'][0]['Reason'],'automatic prefix routing still reports bad SPP length as a length error')
            load_replay(client)
            client.call('/api/control',{'action':'demo'})
            during=run(section()+epb(network(packet(5))))
            check(during['summary']['valid']==1 and client.call('/api/state')['mode']=='demo','background file checking coexists with the live demo without stopping it')
            client.call('/api/control',{'action':'stop'})
            sample_id=start(section()+b''.join(epb(network(packet(i))) for i in range(1,451)))
            sample_report=result(sample_id); sample_csv=client.opener.open(client.base+'/api/captures/export?id='+sample_id+'&format=csv').read().decode('utf-8-sig')
            check(sample_report['summary']['frames']==450 and len(sample_report['records'])==200 and len(list(csv.reader(io.StringIO(sample_csv))))==451,'sample cap does not truncate overall statistics or complete CSV export')
            delete(sample_id)
            bad_samples_id=start(section()+epb(network(malformed))*450); bad_samples=result(bad_samples_id); bad_rows=errors(bad_samples_id)
            check(bad_samples['summary']['abnormalSaved']==450 and len(bad_samples['records'])==200 and len(bad_rows)==450 and bad_rows[-1]['帧号']=='450' and
                  all(bytes.fromhex(r['SPP完整内容HEX'])==malformed[6:] for r in bad_rows),
                  'all abnormal packet bytes are recorded beyond the page sample cap without deduplication')
            delete(bad_samples_id)
            # Exercise the former byte, frame and block caps through real streaming HTTP,
            # without holding a large capture or its CSV in the Python process memory.
            large_path=directory/'large.pcapng'
            with large_path.open('wb') as stream:
                stream.write(section())
                padding=block(0x12345678,b'\0'*(1024*1024-12))
                for _ in range(257):stream.write(padding)
                small=block(0x12345678,b'')*10000
                for _ in range(201):stream.write(small)
                arp=epb(bytes.fromhex('ffffffffffff0011223344550806')+b'\0'*28)
                for _ in range(200):stream.write(arp*1000)
                stream.write(arp)
                stream.write(epb(network(packet(7))))
            with large_path.open('rb') as stream:large_id=start(stream)
            large=result(large_id)
            check(large_path.stat().st_size>256*1024*1024 and large['state']=='completed' and large['bytes']==large['size'],
                  'streaming HTTP upload and verification accept a capture larger than 256MB and two million blocks')
            check(large['summary']['frames']==200002 and large['summary']['other']==200001 and large['summary']['valid']==1 and len(large['records'])==200,
                  'more than 200000 frames are fully counted and the final UDP packet is decoded while page samples stay bounded')
            with client.opener.open(client.base+'/api/captures/export?id='+large_id+'&format=csv',timeout=60) as response:
                rows=csv.reader(io.TextIOWrapper(response,encoding='utf-8-sig')); header=next(rows); count=0; last=None
                for row in rows:count+=1;last=row
            check(count==200002 and last[0]=='200002' and last[8]=='通过',
                  'large-file CSV streams every frame through the final result without truncating at former limits')
            delete(large_id); large_path.unlink()
            bad_parameters=client.call('/api/decoders')['configuration']; bad_parameters['Catalog']['Parameters'][0]['Offset']=5000
            preview=upload_config(client,'field.json',json.dumps(bad_parameters).encode()); client.call('/api/decoders/apply',dict(token=preview['token'],revision=preview['revision']))
            fields_id=start(section()+epb(network(packet(1)))); fields=result(fields_id)
            check(fields['summary']['invalidParameters']==1 and fields['summary']['invalid']==1,'out-of-range parameter mapping is reported separately from SPP structure')
            field_rows=errors(fields_id)
            check(len(field_rows)==1 and field_rows[0]['状态']=='部分参数异常' and bytes.fromhex(field_rows[0]['SPP完整内容HEX'])==packet(1)[6:],
                  'parameter-level abnormalities also preserve the whole SPP packet')
            delete(fields_id)
            many=section()+b''.join(epb(network(packet(i))) for i in range(1,10001))
            long_id=start(many); client.call('/api/captures/cancel?id='+long_id,{})
            stopped=result(long_id)
            check(stopped['state'] in ('cancelled','completed') and (len(many)>8*1024*1024),'streaming upload exceeds the old 8MB import limit and supports cooperative cancellation')
            with client.opener.open(client.base+'/api/captures/export?id='+long_id+'&format=errors') as response:
                check(stopped['state']=='completed' or 'partial' in response.headers['Content-Disposition'],
                      'cancelled abnormal CSV exports are explicitly marked as partial')
            delete(long_id)
            listed=client.call('/api/captures/list'); check(any(j['id']==id for j in listed),'browser refresh can rediscover the current account result')
            delete(id); client.call('/api/captures/status?id='+id,expected=400)
            check(not list((directory/'data/capture-checks').glob('*.pcapng')),'clear-result removes only generated temporary capture files')
            check(not list((directory/'data/capture-checks').glob('*.errors.csv')),'clear-result deletes abnormal temporary CSV files too')
            restored=copy.deepcopy(cfg); restored['Packets'][0].update(ExpectedDatagramLength=0,SppOffset=-1)
            preview=upload_config(client,'restored.json',json.dumps(restored).encode()); client.call('/api/decoders/apply',dict(token=preview['token'],revision=preview['revision']))
            udp_port=free_port(socket.SOCK_DGRAM); settings=client.call('/api/config');settings.update(UdpPort=udp_port,BindAddress='127.0.0.1',SourceIPs=['127.0.0.1'])
            client.call('/api/config',settings);client.call('/api/control',{'action':'start'})
            with socket.socket(socket.AF_INET,socket.SOCK_DGRAM) as sender:
                for real in real_packets:sender.sendto(real,('127.0.0.1',udp_port))
                damaged=bytearray(real_packets[-1]);damaged[20]^=1;sender.sendto(damaged,('127.0.0.1',udp_port))
            wait(lambda:client.call('/api/state')['received']==3,'real Fletcher UDP reception')
            live=client.call('/api/state'); check(live['accepted']==2 and live['invalid']==1,'real UDP listening accepts both user packets and rejects the damaged copy under the same Fletcher rule')
            client.call('/api/control',{'action':'stop'})
            closing_id=start(capture); result(closing_id)
            saved_export=directory/'exported-abnormal.csv'
            saved_export.write_bytes(client.opener.open(client.base+'/api/captures/export?id='+closing_id+'&format=errors').read())
            check(len(list((directory/'data/capture-checks').iterdir()))==3,'one completed job owns a temporary capture, frame CSV and abnormal CSV')
            process.terminate(); process.wait(timeout=6)
            check(not list((directory/'data/capture-checks').iterdir()) and saved_export.exists() and len(list(csv.DictReader(io.StringIO(saved_export.read_text(encoding='utf-8-sig')))))>0,
                  'forced process exit deletes all temporary captures and CSVs while keeping the user-exported CSV')
        finally:
            if process.poll() is None:process.terminate(); process.wait(timeout=6)
    print('All',len(checks),'capture integration checks passed.')


if __name__=='__main__':main()
