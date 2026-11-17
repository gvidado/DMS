"""Runtime dispatcher regression: temporal expressions, globals, independent sources and TC events."""
import json,secrets,shutil,socket,struct,subprocess,tempfile,time,sys
from pathlib import Path
from integration_test import ROOT,Client,free_port,packet,seal,seed_send_monitor,wait,load_replay,replay_fixture

def main():
    executable=Path(sys.argv[1]).resolve() if len(sys.argv)>1 else ROOT/'TelemetryMonitor.exe'
    checks=[]
    def check(ok,label):
        assert ok,label
        checks.append(label);print('PASS',label,flush=True)
    with tempfile.TemporaryDirectory(dir=ROOT/'test-output',prefix='judgment-dispatch-') as temporary:
        root=Path(temporary);shutil.copytree(ROOT/'config',root/'config');shutil.copytree(ROOT/'web',root/'web');seed_send_monitor(root)
        for name in ['decoder.json','continuous-rules.json','tc-bindings.json']:(root/'config'/name).unlink(missing_ok=True)
        (root/'config/observed-commands.json').write_text(json.dumps(dict(SchemaVersion=1,SourceIP='127.0.0.1',IdentityRule='destination-ip-and-udp-payload',NextNumber=1,Commands=[])),encoding='utf-8')
        port=free_port();udp=free_port(socket.SOCK_DGRAM);settings=json.loads((root/'config/server.json').read_text(encoding='utf-8-sig'));settings.update(BindAddress='127.0.0.1',UdpPort=udp,SourceIPs=['127.0.0.1','127.0.0.2'],MappingConfirmed=True,DataTimeoutMs=1800,MulticastGroup='',MulticastInterface='');(root/'config/server.json').write_text(json.dumps(settings),encoding='utf-8')
        process=subprocess.Popen([str(executable),str(root),str(port)],stdin=subprocess.PIPE,stdout=subprocess.DEVNULL,stderr=subprocess.STDOUT,text=True,creationflags=subprocess.CREATE_NO_WINDOW)
        sender=socket.socket(socket.AF_INET,socket.SOCK_DGRAM);sender.bind(('127.0.0.1',0));other=socket.socket(socket.AF_INET,socket.SOCK_DGRAM);other.bind(('127.0.0.2',0));receiver=socket.socket(socket.AF_INET,socket.SOCK_DGRAM);receiver.bind(('127.0.0.2',0));receiver.settimeout(3);seq={1:0,2:0};c=Client(port)
        try:
            def ready():
                try:return c.call('/api/auth/me')['setupRequired']
                except OSError:return False
            wait(ready,'v050 HTTP');password=secrets.token_urlsafe(18);c.call('/api/auth/setup',dict(username='runtime',name='Runtime QA',password=password));c.csrf=c.call('/api/auth/login',dict(username='runtime',password=password))['csrf']
            def state():return c.call('/api/continuous-rules')
            def row(code='A4'):return next(r for r in state()['rows'] if r['rule']['Code']==code)
            def control(action,expected=200):return c.call('/api/continuous-rules/control',dict(action=action),expected=expected)
            def listen(action):return c.call('/api/control',dict(action=action))
            def apply_pending():
                if c.call('/api/configuration/schedule')['pending']:
                    process.stdin.write('tick\n');process.stdin.flush();wait(lambda:not c.call('/api/configuration/schedule')['pending'],'configuration applied')
            def save(script,code='A4',bindings=None,expected=200,source='127.0.0.1',parameter_id=None):
                found=next((r['rule'] for r in state()['rows'] if r['rule']['Code']==code),{})
                rule=dict(found);rule.update(Code=code,Name='Runtime '+code,Level='重要',Enabled=True,Script=script,ParameterId=parameter_id or '工仪全程/'+code,SourceIP=source,Bindings=bindings or {})
                result=c.call('/api/continuous-rules/save',dict(rule=rule,revision=state()['revision']),expected=expected)
                if expected==200:apply_pending()
                return result
            def set_rule(script,code='A4',bindings=None):control('stop');save(script,code,bindings)
            def send(value=-2.5,source=1,jump=1,bad=False,duplicate=False):
                if not duplicate:seq[source]+=jump
                data=packet(seq[source]);struct.pack_into('>f',data,35,value);seal(data)
                if bad:data[-1]^=1
                before=c.call('/api/state')['received'];(sender if source==1 else other).sendto(data,('127.0.0.1',udp));wait(lambda:c.call('/api/state')['received']>before,'live datagram')
            def judged(result,code='A4'):wait(lambda:row(code)['result']==result,'judgment '+code+' '+result)
            def tc():return c.call('/api/continuous-rules/tc')
            def bind(code,expected=200):
                result=c.call('/api/continuous-rules/tc',dict(number=1,code=code,revision=tc()['revision']),expected=expected)
                if expected==200:apply_pending()
                return result
            def command():sender.sendto(b'\x00\x55\xaa\xff',receiver.getsockname());assert receiver.recvfrom(65535)[0]==b'\x00\x55\xaa\xff'
            control('start',409);check(not state()['execution']['running'],'judgment requires an active telemetry source')
            adapter=next(a for a in c.call('/api/send-monitor/interfaces')['interfaces'] if a['loopback']);c.call('/api/send-monitor/config',dict(SourceIPs=['127.0.0.1'],DestinationIPs=['127.0.0.2'],InterfaceId=adapter['id'],DestinationPort=0))
            save('RANGE=[-3 0];');listen('start');send();check(row()['status']=='判读未运行','listening alone never starts judgment')
            control('start');judged('Normal');check(state()['execution']['running'],'explicit start uses latest fresh data')
            control('stop');before=c.call('/api/state')['accepted'];send();check(c.call('/api/state')['accepted']>before and row()['status']=='判读未运行','stopping judgment keeps the listener and history receiving')
            set_rule('VAL=2^3^2+0xA.8+1e-2; EV=522.51,ERR=0.000001;');control('start');judged('Normal');check(True,'right associative powers, hexadecimal fractions and scientific literals')
            math='abs(-2)+min(3,4)+max(3,4)+pow(2,3)+sqrt(9)+sin(0)+cos(0)+tan(0)+asin(0)+acos(1)+exp(0)+log(1)+log10(100)'
            set_rule('VAL='+math+'; EV=24,ERR=0.000001;');control('start');judged('Normal');check(True,'all agreed numerical functions execute against engineering values')
            set_rule('VAL=sqrt(-1); RET=Normal;');control('start');wait(lambda:row()['status']=='执行异常','invalid math');check(not row()['result'],'invalid arithmetic never becomes a normal result')
            for script in ['VAL=this.raw; RET=Normal;','VAL=1&2; RET=Normal;','if(FRAMES(this.value<0,3s)) then RET=Normal; end','if(FRAMES(this.value<TM.A1.value,3)) then RET=Normal; end']:
                control('stop');save(script,expected=400)
            check(True,'raw access, bit operations, time units as frame counts and ambiguous frame anchors reject')
            set_rule('if(this.pre9==NIL && this.value==TM.A4.value && !false) then RET=Normal; else RET=Error; end');control('start');judged('Normal');check(True,'self alias and absent history NIL semantics')
            set_rule('if(HOLD(this.value<0,0.22s)) then RET=Normal; else RET=Unusual; end');send();control('start');judged('Unusual');judged('Normal');check(True,'HOLD matures without another datagram while data remains fresh')
            send(2);judged('Unusual');send();judged('Normal');check(True,'a false condition resets the continuous hold window')
            set_rule('if(HOLD(this.value<0,2s)) then RET=Normal; else RET=Unusual; end');send();control('start');wait(lambda:row()['status']=='等待数据','stale hold',seconds=4);check(row()['result']=='','stale values cannot qualify a hold')
            set_rule('if(FRAMES(this.value<0,3)) then RET=Normal; else RET=Unusual; end');send();control('start');judged('Unusual')
            send();send(duplicate=True);time.sleep(.12);check(row()['timings'][0]['count']==1,'cached data, duplicates and scheduler ticks never count as new frames')
            send();judged('Unusual');send();judged('Normal');check(True,'FRAMES qualifies on exactly three fresh valid samples')
            send(bad=True);time.sleep(.12);check(row()['timings'][0]['count']==0,'checksum failures break consecutive frame qualification')
            send();check(row()['timings'][0]['count']==1,'reception resumes with a new qualification window after invalid data')
            send(jump=2);check(row()['timings'][0]['count']==1,'definite frame loss resets the old frame count before accepting the current sample')
            set_rule('if(FRAMES(TM.A1.value>0,2)) then RET=Normal; else RET=Unusual; end');send();control('start');send();send();judged('Normal');check(True,'single parameter FRAMES infers the referenced parameter as its anchor')
            bindings={'TM.A1':dict(ParameterId='工仪全程/A1',SourceIP='127.0.0.2')}
            set_rule('if(FRAMES(this.value<TM.A1.value,2,TM.A1)) then RET=Normal; else RET=Unusual; end',bindings=bindings);send();send(source=2);control('start');send();check(row()['timings'][0]['count']==0,'an explicit cross-source anchor excludes frames from the main source');send(source=2);send(source=2);judged('Normal');check(any(b['sourceIP']=='127.0.0.2' for b in row()['bindings']),'additional parameters bind to their own source stream')
            control('stop');command();wait(lambda:tc()['total']==1,'learned TC');bind('TC.K001');check(tc()['bindings']==[dict(Code='K001',Number=1)],'a captured complete payload and destination can be manually assigned a TC code')
            set_rule('StateRegion if(TC.K001) then this.state=this.state+1; end EndStateRegion ResultRegion RET=Normal; EndResultRegion');send();control('start')
            for expected in [1,2,3]:command();wait(lambda:row()['currentState']==expected,'repeated actual TC event')
            check(tc()['total']==1 and row()['currentState']==3,'identical actual sends generate distinct TC events while the command catalog remains deduplicated')
            set_rule('StateRegion if(AFTER(TC.K001,0.4s)) then this.state=this.state+1; end EndStateRegion ResultRegion RET=Normal; EndResultRegion');send();control('start');command();time.sleep(.12);command();wait(lambda:row()['currentState']==1,'AFTER deadline',seconds=2);time.sleep(.25);check(row()['currentState']==1,'AFTER fires once at the first deadline and ignores retriggers while pending');command();wait(lambda:row()['currentState']==2,'AFTER rearm');check(True,'AFTER rearms for a subsequent command after firing')
            command();control('stop');time.sleep(.5);control('start');time.sleep(.45);check(row()['currentState']==0,'stop and restart cancel pending command timers and reset rule state')
            listen('stop');listen('start');control('start');command();wait(lambda:row()['currentState']==1,'TC timer before telemetry');check(row()['status']=='等待数据','TC and AFTER state transitions work even before the first telemetry sample');send()
            # Globals are single writer and commit in a batch, independent of rule ordering.
            set_rule('InitRegion gzReady=0; EndInitRegion StateRegion gzReady=1; EndStateRegion ResultRegion RET=Normal; EndResultRegion');save('if(gzReady==1) then RET=Normal; else RET=Error; end',code='A1');send();control('start');judged('Normal','A1');check(state()['execution']['globals']['gzReady']==1,'global writes commit and wake dependent rules on the next dispatch')
            control('stop');save('gzReady=2; RET=Normal;',code='A1');control('start',400);check(not state()['execution']['running'],'multiple global writers reject the whole start operation')
            save('gzOther=gzReady; RET=Normal;',code='A1');save('gzReady=gzOther; RET=Normal;');control('start',400);check(not state()['execution']['running'],'inter-rule global dependency cycles cannot start')
            save('RET=Normal;',code='A1');save('if(OSTIME>=0.12 && YEAR>=2026 && MONTH>=1 && DAY>=1 && HOUR>=0 && MINUTE>=0 && SECOND>=0) then RET=Normal; else RET=Unusual; end');send();control('start');judged('Normal');check(True,'elapsed and calendar clock expressions advance without incoming data')
            listen('stop');check(not state()['execution']['running'],'stopping reception automatically stops judgment');listen('start');check(not state()['execution']['running'],'a new listening session never restarts judgment automatically');listen('stop')
            # Internal pcap replay uses capture-time seconds, including live speed changes.
            load_replay(c,replay_fixture(count=80,interval_us=100000));save('if(HOLD(this.value<0,1s)) then RET=Normal; else RET=Unusual; end');c.call('/api/replay/speed',dict(speed=5));listen('demo');control('start');started=time.monotonic();judged('Normal');check(time.monotonic()-started<.7 and float(state()['execution']['elapsedSeconds'])>=1,'5x pcap replay advances temporal conditions in original capture time');listen('stop');check(not state()['execution']['running'],'stopping replay cancels judgment too')
        finally:
            sender.close();other.close();receiver.close()
            if process.poll() is None:process.terminate();process.wait(timeout=6)
    (ROOT/'test-output/release-v0.9.7/judgment-report.json').write_text(json.dumps(dict(checks=len(checks),details=checks),ensure_ascii=False,indent=2),encoding='utf-8')
    print('All',len(checks),'judgment dispatch checks passed.')

if __name__=='__main__':main()
