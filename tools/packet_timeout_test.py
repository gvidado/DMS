"""Real local UDP: enabled SPP definitions have independent valid-new-frame timeouts."""
import copy,json,secrets,shutil,socket,subprocess,sys,tempfile,threading,time
from pathlib import Path
from integration_test import ROOT,Client,free_port,packet,wait,seed_send_monitor
from decoder_integration_test import upload

def main():
    exe=Path(sys.argv[1]).resolve();checks=[]
    def check(ok,name):assert ok,name;checks.append(name);print('PASS',name,flush=True)
    with tempfile.TemporaryDirectory(prefix='packet-timeout-',dir=ROOT/'test-output') as temp:
        root=Path(temp);shutil.copytree(ROOT/'config',root/'config');shutil.copytree(ROOT/'web',root/'web');seed_send_monitor(root)
        for filename in ['decoder.json','continuous-rules.json','tc-bindings.json','observed-commands.json']:(root/'config'/filename).unlink(missing_ok=True)
        port=free_port();udp=free_port(socket.SOCK_DGRAM);c=Client(port);password=secrets.token_urlsafe(18);healthy_stop=threading.Event();healthy=None
        def launch():return subprocess.Popen([str(exe),'--local','--root',str(root),'--web-port',str(port)],stdout=subprocess.DEVNULL,stderr=subprocess.STDOUT)
        def ready():
            try:c.call('/api/auth/me');return True
            except OSError:return False
        def alarms():return [r for r in c.call('/api/alarms/list?pageSize=100')['records'] if r['category']=='SPP超时']
        def b_alarms():return [r for r in alarms() if r['packet']=='包B']
        def health(name):return next(r['health'] for r in c.call('/api/state')['packetStatistics'] if r['name']==name)
        def send(payload,source='127.0.0.1'):
            with socket.socket(socket.AF_INET,socket.SOCK_DGRAM) as s:s.bind((source,0));s.sendto(payload,('127.0.0.1',udp))
        def run_healthy():
            nonlocal healthy
            healthy_stop.clear()
            def run():
                n=100
                with socket.socket(socket.AF_INET,socket.SOCK_DGRAM) as sender:
                    while not healthy_stop.is_set():sender.sendto(packet(n),('127.0.0.1',udp));n=(n+1)%16384;healthy_stop.wait(.06)
            healthy=threading.Thread(target=run);healthy.start()
        def stop_healthy():
            healthy_stop.set()
            if healthy:healthy.join(timeout=3)
        def spam(payload,seconds=1.15,source='127.0.0.1'):
            end=time.monotonic()+seconds
            while time.monotonic()<end:send(payload,source);time.sleep(.08)
        process=launch()
        try:
            wait(ready,'packet timeout service');c.call('/api/auth/setup',dict(username='timeout_qa',name='超时确认人',password=password));c.csrf=c.call('/api/auth/login',dict(username='timeout_qa',password=password))['csrf']
            check(c.call('/api/config')['EnablePacketTimeoutAlarm'] is False,'existing configurations default to packet timeout alarm disabled')
            config=c.call('/api/decoders')['configuration'];config['Catalog']['Parameters']=[p for p in config['Catalog']['Parameters'] if p['Group']=='工仪全程' and p['Code'] in ('A1','A4')];base=config['Packets'][0];config['Packets']=[]
            for n,name in enumerate(['包A','包B','禁用包C']):
                d=copy.deepcopy(base);d.update(Id='watch-'+str(n),Name=name,Apid=8+n,Enabled=n<2,MappingConfirmed=True,SppOffset=-1,ExpectedDatagramLength=0);config['Packets'].append(d)
            preview=upload(c,'timeout.json',json.dumps(config).encode());c.call('/api/decoders/apply',dict(token=preview['token'],revision=preview['revision']))
            cfg=c.call('/api/config');cfg.update(BindAddress='127.0.0.1',UdpPort=udp,SourceIPs=['127.0.0.1'],MulticastGroup='',MulticastInterface='',DataTimeoutMs=650,EnablePacketTimeoutAlarm=False);c.call('/api/config',cfg)
            c.call('/api/control',dict(action='start'));run_healthy();time.sleep(1.4);check(alarms()==[] and c.call('/api/state')['sources'][0]['online'],'unchecked option adds no packet-level alarm when a device is active');c.call('/api/control',dict(action='stop'));stop_healthy()
            cfg['EnablePacketTimeoutAlarm']=True;c.call('/api/config',cfg);c.call('/api/control',dict(action='start'));run_healthy();wait(lambda:len(b_alarms())==1,'never-received B timeout')
            check(health('包A')['state']=='正常' and health('包B')['state']=='超时异常' and c.call('/api/state')['sources'][0]['online'] and all(r['packet']!='禁用包C' for r in alarms()),'missing B alarms independently while A and the source remain healthy; disabled C is excluded')
            first=b_alarms()[0];c.call('/api/alarms/ack',dict(id=first['id']));time.sleep(.9);check(len(b_alarms())==1 and b_alarms()[0]['confirmed'],'confirming a continuous packet timeout retains the episode without retriggering')
            send(packet(10,9));wait(lambda:health('包B')['state']=='正常','B valid recovery');last=health('包B')['lastValid'];spam(packet(10,9));wait(lambda:health('包B')['state']=='超时异常','B duplicate stream timeout')
            check(health('包B')['lastValid']==last and health('包A')['state']=='正常','duplicate-only traffic does not renew B freshness or affect A')
            bad=packet(11,9);bad[-1]^=1;spam(bad);check(len(b_alarms())==1 and b_alarms()[0]['confirmed'] and health('包B')['state']=='超时异常' and health('包B')['lastValid']==last,'corrupt checksums cannot recover a packet or add a second timeout alarm in this listening run')
            send(packet(11,9));wait(lambda:health('包B')['state']=='正常','B second recovery');last=health('包B')['lastValid'];spam(packet(9,9));wait(lambda:health('包B')['state']=='超时异常','old frames timeout');check(len(b_alarms())==1 and health('包B')['lastValid']==last,'old frames do not renew freshness and recurrence keeps the first timeout record')
            send(packet(12,9));wait(lambda:health('包B')['state']=='正常','B third recovery');spam(packet(13,9),source='127.0.0.2');wait(lambda:health('包B')['state']=='超时异常','filtered source timeout');check(len(b_alarms())==1 and health('包B')['state']=='超时异常','filtered traffic cannot prevent timeout and repeated recovery does not reset alarm deduplication')
            c.call('/api/control',dict(action='stop'));stop_healthy();before=len(alarms());time.sleep(.9);check(len(alarms())==before and health('包A')['active'] is False,'stopping reception stops timeout checks')
            c.call('/api/control',dict(action='start'));check(health('包B')['lastValid']=='—' and health('包B')['state']=='等待有效新帧','a new listening session clears previous freshness and starts its own deadline');wait(lambda:len(alarms())==before+2,'both enabled packets timeout after restart');check({r['packet'] for r in alarms()}=={'包A','包B'},'each enabled packet is checked from start even when no packet arrives')
            c.call('/api/control',dict(action='stop'));process.terminate();process.wait(timeout=6);process=launch();c=Client(port);wait(ready,'packet setting persistence');c.csrf=c.call('/api/auth/login',dict(username='timeout_qa',password=password))['csrf'];check(c.call('/api/config')['EnablePacketTimeoutAlarm'] is True,'packet timeout option persists across server restart')
        finally:
            stop_healthy()
            if process.poll() is None:process.terminate();process.wait(timeout=6)
    print('All',len(checks),'packet timeout checks passed.')
if __name__=='__main__':main()
