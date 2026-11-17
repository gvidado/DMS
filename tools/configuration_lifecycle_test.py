"""Pending state recovery, cancellation and failed endpoint adoption in isolated roots."""
import json,secrets,shutil,socket,subprocess,sys,tempfile,time
from pathlib import Path
from integration_test import ROOT,Client,free_port

def main():
 host=Path(sys.argv[1]).resolve();checks=[]
 def check(ok,label):assert ok,label;checks.append(label);print('PASS',label,flush=True)
 def until(fn,label):
  for _ in range(250):
   value=fn()
   if value:return value
   time.sleep(.04)
  raise AssertionError(label)
 with tempfile.TemporaryDirectory(dir=ROOT/'test-output',prefix='configuration-life-') as scratch:
  root=Path(scratch);shutil.copytree(ROOT/'config',root/'config');shutil.copytree(ROOT/'web',root/'web');port=free_port();udp=free_port(socket.SOCK_DGRAM);password=secrets.token_urlsafe(16)
  settings=json.loads((root/'config/server.json').read_text(encoding='utf-8-sig'));settings.update(BindAddress='127.0.0.1',UdpPort=udp,WebPort=port,SourceIPs=['127.0.0.1'],MulticastGroup='',MulticastInterface='');(root/'config/server.json').write_text(json.dumps(settings),encoding='utf-8');(root/'config/send-monitor.json').write_text(json.dumps(dict(SourceIP='127.0.0.1',DestinationIP='127.0.0.2',DestinationPort=0,InterfaceId='')),encoding='utf-8')
  proc=None;c=None
  def start(first=False):
   nonlocal proc,c
   proc=subprocess.Popen([str(host),str(root),str(port)],stdin=subprocess.PIPE,stdout=subprocess.DEVNULL,stderr=subprocess.STDOUT,text=True,creationflags=subprocess.CREATE_NO_WINDOW);c=Client(port)
   def ready():
    try:return c.call('/api/auth/me')
    except OSError:return False
   until(ready,'server startup')
   if first:c.call('/api/auth/setup',dict(username='lifecycle',name='生命周期测试',password=password))
   c.csrf=c.call('/api/auth/login',dict(username='lifecycle',password=password))['csrf']
  def tick():proc.stdin.write('tick\n');proc.stdin.flush()
  def stop():
   if proc and proc.poll() is None:proc.stdin.write('quit\n');proc.stdin.flush();proc.wait(8)
  def stage(**kw):
   config=c.call('/api/config?draft=1');config.update(kw);c.call('/api/config',config);until(lambda:c.call('/api/configuration/schedule')['pending'],'pending')
  try:
   start(True);stage(FramePeriodMs=321);stop();start();check(c.call('/api/configuration/schedule')['pending'] and c.call('/api/config?draft=1')['FramePeriodMs']==321 and c.call('/api/config')['FramePeriodMs']!=321,'pending settings survive process restart without applying early')
   c.call('/api/configuration/cancel',{});check(not c.call('/api/configuration/schedule')['pending'] and c.call('/api/config')['FramePeriodMs']!=321,'cancellation removes pending state and preserves effective configuration')
   c.call('/api/control',dict(action='start'));occupied=socket.socket(socket.AF_INET,socket.SOCK_DGRAM);occupied.bind(('127.0.0.1',0));stage(UdpPort=occupied.getsockname()[1]);original=(root/'config/server.json').read_bytes();tick();failure=until(lambda:c.call('/api/configuration/schedule')['error'],'failed application');check('本轮配置未生效' in failure and c.call('/api/config')['UdpPort']==udp and c.call('/api/state')['mode']=='live' and (root/'config/server.json').read_bytes()==original,'occupied destination port rejects the batch while effective files and reception stay unchanged')
   sender=socket.socket(socket.AF_INET,socket.SOCK_DGRAM);sender.sendto(b'bad-udp',('127.0.0.1',udp));until(lambda:c.call('/api/state')['received']>0,'old port still receives');check(True,'the original UDP port continues receiving after failed adoption');before=c.call('/api/state')['received']
   new_port=free_port(socket.SOCK_DGRAM);stage(UdpPort=new_port,FramePeriodMs=322);tick();until(lambda:not c.call('/api/configuration/schedule')['pending'],'corrected batch');sender.sendto(b'bad-udp',('127.0.0.1',new_port));until(lambda:c.call('/api/state')['received']>before,'new port receives');check(c.call('/api/config')['FramePeriodMs']==322 and not c.call('/api/configuration/schedule')['error'],'corrected pending settings apply on the next cycle and preserve counters')
   sender.close();occupied.close();c.call('/api/control',dict(action='stop'));stage(FramePeriodMs=323);tick();until(lambda:not c.call('/api/configuration/schedule')['pending'],'stopped adoption');check(c.call('/api/config')['FramePeriodMs']==323 and c.call('/api/state')['mode']=='stopped','the same schedule applies saved settings when reception is stopped')
   (ROOT/'test-output/release-v0.9.4/lifecycle-report.json').write_text(json.dumps(dict(checks=len(checks),details=checks),ensure_ascii=False,indent=2),encoding='utf-8')
  finally:stop()

if __name__=='__main__':main()
