"""Real UDP rollback recovery for independent TME/0202-TMB/0422-TMB streams."""
import copy,csv,io,json,secrets,shutil,socket,struct,subprocess,sys,tempfile,time,urllib.request
from pathlib import Path
from integration_test import ROOT,Client,free_port,wait,seal
from runtime_benchmark import seed
from runtime_load_test import packet


def telemetry(outer,sequence,value):
    raw=packet(2,0,sequence);raw[0:2]=outer.to_bytes(2,'big');raw[22:24]=value.to_bytes(2,'big');return bytes(seal(raw))


def pcap(payloads,port):
    def block(kind,body):
        body+=b'\0'*(-len(body)%4);n=len(body)+12;return struct.pack('<II',kind,n)+body+struct.pack('<I',n)
    parts=[block(0x0a0d0d0a,struct.pack('<IHHq',0x1a2b3c4d,1,0,-1)),block(1,struct.pack('<HHI',101,0,65535))]
    for i,payload in enumerate(payloads):
        udp=struct.pack('>HHHH',6000,port,len(payload)+8,0)+payload
        data=struct.pack('>BBHHHBBH4s4s',0x45,0,len(udp)+20,0,0,64,17,0,socket.inet_aton('127.0.0.1'),socket.inet_aton('127.0.0.1'))+udp
        ticks=1_700_000_000_000000+i*160000;parts.append(block(6,struct.pack('<IIIII',0,ticks>>32,ticks&0xffffffff,len(data),len(data))+data))
    return b''.join(parts)


def main():
    exe=Path(sys.argv[1]).resolve();baseline='--expect-blocked' in sys.argv;checks=[]
    def check(ok,label):
        assert ok,label
        checks.append(label);print('PASS',label,flush=True)
    with tempfile.TemporaryDirectory(dir=ROOT/'test-output',prefix='telemetry-resync-') as temp:
        root=Path(temp);seed(root,per_packet=2,packets=1)
        for folder in ['web','native','database']:shutil.copytree(ROOT/folder,root/folder)
        def save(name,value):(root/'config'/name).write_text(json.dumps(value),encoding='utf-8')
        cfg=json.loads((root/'config/decoder.json').read_text(encoding='utf-8'));base=cfg['Packets'][0];base.update(Name='TME正常流',MonitorRaw=False)
        b0=copy.deepcopy(base);b0.update(Id='b0',Name='TMB0202回退流',OuterSourceId=0x0202)
        b1=copy.deepcopy(base);b1.update(Id='b1',Name='TMB0422回退流',OuterSourceId=0x0422,Groups=['备用'])
        field=copy.deepcopy(cfg['Catalog']['Parameters'][0]);field.update(Id='备用/A2',Code='A2',Group='备用',Name='备用流参数')
        cfg['Catalog']['Parameters'].append(field);cfg['Catalog']['Sheets'].append(dict(Name='备用',Index=3));cfg['Packets']+=[b0,b1];save('decoder.json',cfg)
        rules=json.loads((root/'config/continuous-rules.json').read_text(encoding='utf-8'))
        for pid,code,fid in [('b0','TMB.A0','表0/A0'),('b1','TMB.A2','备用/A2')]:
            rule=copy.deepcopy(rules['Rules'][0]);rule.update(Id=pid+'-rule',ParameterId=pid+'::TMB::'+fid,Code=code,Name=code);rules['Rules'].append(rule)
        save('continuous-rules.json',rules)
        settings=json.loads((root/'config/server.json').read_text(encoding='utf-8-sig'));settings.update(UdpPort=free_port(socket.SOCK_DGRAM),DataTimeoutMs=500);save('server.json',settings)
        sid='d'*32;save('sequences.json',dict(SchemaVersion=1,Revision='resync-fixture',DefaultTimeoutSeconds=5,NextJudgeNumber=2,NextExecuteNumber=1,Sequences=[dict(
            Id=sid,Code='XLP0001',Name='等待回退后的TMB新帧',Mode='judge',SourceIP='127.0.0.1',Rows=[dict(Row=2,Keyword='',Description='回退后首次新帧即可通过',Command='',Condition='TMB.A0.value >= 201',Mark='',Expected='.2',Timeout='5')])]))
        c=Client(free_port());password=secrets.token_urlsafe(18);log=(root/'server.log').open('wb')
        process=subprocess.Popen([str(exe),'--local','--root',str(root),'--web-port',c.base.rsplit(':',1)[1]],stdout=log,stderr=subprocess.STDOUT,creationflags=subprocess.CREATE_NO_WINDOW)
        sender=socket.socket(socket.AF_INET,socket.SOCK_DGRAM)
        ids={'e':'p0::TME::表0/A0','b0':'b0::TMB::表0/A0','b1':'b1::TMB::备用/A2'}
        def state():return c.call('/api/state')
        def values(s=None):return {r['id']:r for r in (s or state())['rows']}
        def stats(pid,s=None):return next(row for row in (s or state())['packetStatistics'] if row['id']==pid)
        def send(outer,sequence,value=100,damaged=False,sock=sender):
            before=state();raw=bytearray(telemetry(outer,sequence,value))
            if damaged:raw[22]^=1
            sock.sendto(raw,('127.0.0.1',settings['UdpPort']));wait(lambda:state()['received']>before['received'],'UDP received')
            return before,state()
        eseq=5000
        def pump_e(seconds):
            nonlocal eseq
            end=time.monotonic()+seconds
            while time.monotonic()<end:eseq+=1;send(0x0802,eseq);time.sleep(.04)
        def ready():
            try:c.call('/api/auth/me');return True
            except OSError:return False
        try:
            wait(ready,'resync server');c.call('/api/auth/setup',dict(username='resyncqa',name='计数回退验证',password=password));c.csrf=c.call('/api/auth/login',dict(username='resyncqa',password=password))['csrf']
            c.call('/api/selection',dict(ids=list(ids.values())));c.call('/api/control',dict(action='start'));c.call('/api/continuous-rules/control',dict(action='start'))
            send(0x0802,5000,100);send(0x0202,5000,200);send(0x0422,5000,300)
            initial=state();check(initial['accepted']==3 and all(values(initial)[pid]['status']=='有效' for pid in ids.values()),'same IP/APID/sequence is independently accepted by TME, 0202 TMB and 0422 TMB')
            if not baseline:
                for pid in ('p0', 'b0', 'b1'):
                    row = stats(pid, initial)
                    check(row['lastMatch'] == row['lastValidFrame'] == row['lastParameterUpdate'] and row['lastOutcome'] == '参数已更新',
                          'same-frame matching, validity and parameter times are independently visible for ' + pid)
            pump_e(.75);check(values()[ids['e']]['status']=='有效' and all(values()[ids[key]]['status']=='过期' for key in ['b0','b1']),'fixture reproduces TME valid while both TMB streams are stale without restarting reception')
            seq=c.call('/api/sequence/config');run=c.call('/api/sequence/control',dict(action='start',id=sid,revision=seq['revision'],requestId=secrets.token_hex(16),runName='TMB回退验证'))['runId']
            wait(lambda:c.call('/api/sequence/state?runId='+run)['rows'][0]['status']=='等待参数','sequence waits for fresh TMB')
            pump_e(.05);before,after=send(0x0202,3125,201)
            if baseline:
                check(after['accepted']==before['accepted'] and after['outOfOrder']==before['outOfOrder']+1 and values(after)[ids['b0']]['status']=='过期','baseline rejects the real checksum-valid 1875-frame rollback and leaves TMB stale')
                before,after=send(0x0422,1250,301)
                check(after['accepted']==before['accepted'] and values(after)[ids['b1']]['status']=='过期','baseline also rejects the 3750-frame rollback while TME remains independent')
                print(json.dumps(dict(baseline=True,checks=len(checks)),ensure_ascii=True));return
            check(after['accepted']==before['accepted']+1 and values(after)[ids['b0']]['status']=='有效' and values(after)[ids['b0']]['value']=='201','0202 TMB immediately refreshes on its first valid frame 1875 counts behind')
            check(values(after)[ids['b1']]['status']=='过期','fresh 0202 data cannot make the independent 0422 stream fresh')
            wait(lambda:not c.call('/api/sequence/state?runId='+run)['runtime']['running'],'sequence finishes after first rebased packet')
            check(c.call('/api/sequence/state?runId='+run)['runtime']['abnormal']==0,'sequence judgment consumes the first rebased TMB sample and passes without a listening restart')
            before,after=send(0x0422,1250,301)
            check(after['accepted']==before['accepted']+1 and values(after)[ids['b1']]['status']=='有效' and values(after)[ids['b1']]['value']=='301','0422 TMB immediately refreshes on its first valid frame 3750 counts behind')
            check(stats('b0')['lastResyncFrom']==5000 and stats('b0')['lastResyncTo']==3125 and stats('b1')['lastResyncTo']==1250 and stats('b0')['lastResyncAt'],'per-SPP diagnostics retain independent resync origin, destination and timestamp')
            send(0x0202,3126,201);send(0x0422,1251,301)
            wait(lambda:all(row['result']=='Normal' for row in c.call('/api/continuous-rules')['rows'] if row['rule']['Enabled'] and row['rule']['Code'].startswith('TMB.')),'continuous TMB judgment processes fresh values')
            check(True,'continuous TMB judgment recovers with the new values')
            before,after=send(0x0202,3125,999)
            check(after['accepted']==before['accepted'] and after['duplicates']==before['duplicates']+1 and values(after)[ids['b0']]['value']=='201','a duplicate of the rebased frame cannot overwrite the value')
            check(stats('b0',after)['lastParameterUpdate']==stats('b0',before)['lastParameterUpdate']
                  and '重复' in stats('b0',after)['lastOutcome'],'duplicate records a reason without changing the last parameter update')
            for n in range(3126,3130):send(0x0202,n,n)
            before,after=send(0x0202,3125,999)
            check(after['duplicates']==before['duplicates']+1 and values(after)[ids['b0']]['value']=='3129','the fifth most recent received sequence is still rejected as a duplicate')
            send(0x0202,3130,3130);before,after=send(0x0202,3126,2126)
            check(after['accepted']==before['accepted']+1 and values(after)[ids['b0']]['value']=='2126','a backward sequence outside the five-reception window is accepted and refreshes the real decoded parameter')
            before,after=send(0x0202,3127,777,damaged=True)
            check(after['invalid']==before['invalid']+1 and after['accepted']==before['accepted'] and values(after)[ids['b0']]['value']=='2126','Fletcher failure still rejects data and preserves the previous decoded value')
            check(stats('b0',after)['lastValidFrame']==stats('b0',before)['lastValidFrame']
                  and '校验' in stats('b0',after)['lastOutcome'],'checksum failure is visible only on the matching stream')
            before,after=send(0x0202,3127,2127)
            check(after['accepted']==before['accepted']+1 and values(after)[ids['b0']]['value']=='2127','a damaged packet does not poison the recent-five sequence window')
            send(0x0422,16382,310);counter=stats('b1')['outOfOrder']
            for n in [16383,0,1]:send(0x0422,n,311+n%2)
            check(stats('b1')['outOfOrder']==counter and stats('b1')['lastSequence']==1,'natural 14-bit wrap remains valid without resync or stale suppression')
            before,after=send(0x0422,10000,320)
            check(after['accepted']==before['accepted']+1 and after['lost']==before['lost'] and values(after)[ids['b1']]['value']=='320','a large ambiguous jump refreshes data without fabricating a huge missing-frame count')
            with socket.socket(socket.AF_INET,socket.SOCK_DGRAM) as other:
                other.bind(('127.0.0.2',0));before,after=send(0x0202,3128,888,sock=other)
                check(after['filtered']==before['filtered']+1 and after['accepted']==before['accepted'] and values(after)[ids['b0']]['value']=='2127','source IP filtering remains strict')
            alarms=c.call('/api/alarms/list')['records'];sync=[row for row in alarms if row['category']=='计数重同步']
            check(len(sync)==2 and all('继续解码' in row['message'] for row in sync),'each affected SPP records only one resync alarm in this listening session, with truthful accepted-data wording')
            capture=pcap([telemetry(0x0802,5000,100),telemetry(0x0202,5000,200),telemetry(0x0802,5001,101),telemetry(0x0202,3125,201),telemetry(0x0202,3125,999),telemetry(0x0202,3126,202),telemetry(0x0422,5000,300),telemetry(0x0422,1250,301),telemetry(0x0422,1251,302)],settings['UdpPort'])
            request=urllib.request.Request(c.base+'/api/captures/start?filename=resync.pcapng',data=capture,headers={'Content-Type':'application/octet-stream','X-CSRF-Token':c.csrf})
            job=json.loads(c.opener.open(request).read())['id'];wait(lambda:c.call('/api/captures/status?id='+job)['state'] in ['completed','failed'],'pcapng resync check')
            result=c.call('/api/captures/status?id='+job+'&full=true');summary=result['summary']
            check(result['state']=='completed' and summary['valid']==8 and summary['duplicates']==1 and summary['outOfOrder']==summary['resets']==2,'pcapng verification applies the same accept/rebase/duplicate policy as real reception')
            check(next(p for p in result['parameters'] if p['Code']=='TMB.A0')['Value']=='202','file-check decoding continues after counter rollback')
            rows=list(csv.reader(io.StringIO(c.opener.open(c.base+'/api/captures/export?id='+job+'&format=csv').read().decode('utf-8-sig'))))
            check(rows[4][8]=='通过' and '重新同步' in rows[4][9] and rows[5][8]=='重复帧','file-check CSV reports accepted resync frames and still identifies actual duplicates')
            if '--browser' in sys.argv:
                runtime=Path.home()/'.cache/codex-runtimes/codex-primary-runtime/dependencies/node'
                data=dict(base=c.base,password=password,screenshot=str(exe.parent/'resync-overview.png'))
                browser=subprocess.run([str(runtime/'bin/node.exe'),str(ROOT/'tools/telemetry_resync_browser.mjs'),str(runtime)],input=json.dumps(data),text=True,encoding='utf-8',capture_output=True,timeout=60,creationflags=subprocess.CREATE_NO_WINDOW)
                assert browser.returncode==0,browser.stdout+browser.stderr
                check('PASS' in browser.stdout,'browser displays each SPP new-frame count, current sequence and latest resync details')
            c.call('/api/control',dict(action='stop'))
            (exe.parent/'telemetry-resync-report.json').write_text(json.dumps(dict(checks=len(checks),details=checks),ensure_ascii=False,indent=2),encoding='utf-8')
        except Exception:
            try:print(json.dumps(c.call('/api/continuous-rules')['rows'],ensure_ascii=True)[-6000:])
            except Exception:pass
            log.flush();print((root/'server.log').read_text(encoding='utf-8',errors='replace')[-2500:]);raise
        finally:
            sender.close();process.terminate();process.wait(10);log.close()
    print('All',len(checks),'telemetry resync checks passed. Only loopback UDP used.')


if __name__=='__main__':main()
