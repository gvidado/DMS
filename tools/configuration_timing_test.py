"""Immediate stopped saves, running 120s batching and immediate application on reception stop."""
import json,secrets,shutil,socket,subprocess,sys,tempfile,time
from pathlib import Path
from integration_test import ROOT,Client,free_port,seed_send_monitor

def main():
 host=Path(sys.argv[1]).resolve();out=ROOT/'test-output/configuration-timing';checks=[]
 def check(ok,label):assert ok,label;checks.append(label);print('PASS',label,flush=True)
 def until(fn,label):
  for _ in range(250):
   result=fn()
   if result:return result
   time.sleep(.04)
  raise AssertionError(label)
 with tempfile.TemporaryDirectory(dir=ROOT/'test-output',prefix='configuration-timing-') as scratch:
  root=Path(scratch);shutil.copytree(ROOT/'config',root/'config');shutil.copytree(ROOT/'web',root/'web');port=free_port();udp=free_port(socket.SOCK_DGRAM);password=secrets.token_urlsafe(16)
  def save(name,value):(root/'config'/name).write_text(json.dumps(value),encoding='utf-8')
  for name in ['decoder.json','continuous-rules.json','tc-bindings.json','sequences.json','observed-commands.json']:(root/'config'/name).unlink(missing_ok=True)
  settings=json.loads((root/'config/server.json').read_text(encoding='utf-8-sig'));settings.update(BindAddress='127.0.0.1',UdpPort=udp,WebPort=port,SourceIPs=['127.0.0.1'],MulticastGroup='',MulticastInterface='');save('server.json',settings);seed_send_monitor(root)
  save('decoder.json',dict(SchemaVersion=2,Revision='timing-decoder',Catalog=dict(Parameters=[dict(Id='test/A1',Code='A1',Name='参数一',Group='测试',Offset=0,Length=2,BitStart=-1,BitCount=0,Type='uint',Scale=1,Bias=0,Precision=0,Format='十进制',Unit='V',Note='',Supported=True,Enums={},EnumRanges=[])],Sheets=[dict(Name='测试',Index=2)]),Packets=[dict(Id='e',Name='原包名',OuterSourceId=2050,SourceIP='127.0.0.1',Enabled=True,MappingConfirmed=True,Apid=8,ExpectedLengthField=-1,Groups=['测试'])]))
  save('observed-commands.json',dict(SourceIP='127.0.0.1',NextNumber=2,Commands=[dict(Number=1,Name='旧名称',DestinationIP='127.0.0.2',DestinationIPs=['127.0.0.2'],PayloadHex='A1 01')]))
  save('tc-bindings.json',dict(Revision='timing-tc',Bindings=[dict(Number=1,Code='K001')]));save('command-send.json',dict(SourceIP='127.0.0.1',Routes=[dict(DestinationIP='127.0.0.2',SourcePort=free_port(socket.SOCK_DGRAM),DestinationPort=free_port(socket.SOCK_DGRAM))]))
  definition=dict(Id='a'*32,Code='XLP0001',Name='编辑预览保留',Mode='judge',SourceIP='127.0.0.1',Rows=[dict(Row=2,Keyword='TEXT',Description='初始')]);save('sequences.json',dict(Revision='timing-sequence',Sequences=[definition],DefaultTimeoutSeconds=30,NextJudgeNumber=2,NextExecuteNumber=1))
  proc=None;c=None
  def start(first=False):
   nonlocal proc,c
   proc=subprocess.Popen([str(host),str(root),str(port)],stdin=subprocess.PIPE,stdout=subprocess.DEVNULL,stderr=subprocess.STDOUT,text=True,creationflags=subprocess.CREATE_NO_WINDOW);c=Client(port)
   def ready():
    try:return c.call('/api/auth/me')
    except OSError:return False
   until(ready,'startup')
   if first:c.call('/api/auth/setup',dict(username='timingqa',name='时机测试',password=password))
   c.csrf=c.call('/api/auth/login',dict(username='timingqa',password=password))['csrf']
  def post_settings(**changes):
   value=c.call('/api/config?draft=1');value.update(changes);return c.call('/api/config',value)
  def tick():proc.stdin.write('tick\n');proc.stdin.flush()
  def stop_process():
   if proc and proc.poll() is None:proc.stdin.write('quit\n');proc.stdin.flush();proc.wait(8)
  try:
   start(True);post_settings(FramePeriodMs=321);s=c.call('/api/configuration/schedule');check(c.call('/api/config')['FramePeriodMs']==321 and not s['pending'] and not s['monitoring'] and s['nextInSeconds']==0 and s['intervalSeconds']==120,'stopped settings save is effective before the response and advertises the 120-second running interval')
   d=c.call('/api/decoders?draft=1');field=d['configuration']['Catalog']['Parameters'][0];field['Scale']=2;c.call('/api/decoders/parameter',dict(id=field['Id'],revision=d['revision'],parameter=field));check(c.call('/api/decoders')['configuration']['Catalog']['Parameters'][0]['Scale']==2 and not c.call('/api/configuration/schedule')['pending'],'stopped parameter edits become effective immediately')
   rules=c.call('/api/continuous-rules?draft=1');rule=rules['rows'][0]['rule'];rule['Level']='重要';c.call('/api/continuous-rules/save',dict(rule=rule,revision=rules['revision']));check(c.call('/api/continuous-rules')['rows'][0]['rule']['Level']=='重要','stopped judgment rules become effective immediately')
   for name in ['新名称一','新名称二']:
    library=c.call('/api/send-monitor/commands/list?draft=1');c.call('/api/send-monitor/commands/edit',dict(number=1,revision=library['revision'],originalCode='',code='',name=name))
   check(c.call('/api/send-monitor/commands/list')['rows'][0]['name']=='新名称二','consecutive stopped command edits retain usable preview revisions')
   tx=c.call('/api/command-send/config?draft=1')
   for i in range(3):
    tx['configuration']['Routes'][0]['DestinationPort']=free_port(socket.SOCK_DGRAM);tx=c.call('/api/command-send/config',dict(configuration=tx['configuration'],revision=tx['revision']))
   c.call('/api/command-send/config',dict(configuration=tx['configuration'],revision=tx['revision']));tx['configuration']['Routes'][0]['DestinationPort']=free_port(socket.SOCK_DGRAM);c.call('/api/command-send/config',dict(configuration=tx['configuration'],revision=tx['revision']));check(True,'consecutive stopped transmit-port saves, including an unchanged save, return usable configuration revisions')
   cfg=c.call('/api/sequence/config?draft=1');preview=c.call('/api/sequence/edit-preview',dict(id=definition['Id'],revision=cfg['revision']));rows=preview['definitions'][0]['definition']['Rows'];rows.append(dict(Row=3,Keyword='TEXT',Description='立即入库'));preview=c.call('/api/sequence/recheck',dict(token=preview['token'],id=definition['Id'],rows=rows,nextRowNumber=4));c.call('/api/sequence/save-edits',dict(token=preview['token']));check(len(c.call('/api/sequence/definition?id='+definition['Id'])['definition']['Rows'])==2,'stopped sequence row edits also take effect immediately')
   c.call('/api/control',dict(action='start'));post_settings(FramePeriodMs=322);d=c.call('/api/decoders?draft=1');packet=d['configuration']['Packets'][0];packet['Name']='停止时生效的新包名';c.call('/api/decoders/packet',dict(packet=packet,revision=d['revision']));check(c.call('/api/config')['FramePeriodMs']==321 and c.call('/api/configuration/schedule')['pending'] and c.call('/api/state')['mode']=='live','running saves remain pending without changing current reception')
   sender=socket.socket(socket.AF_INET,socket.SOCK_DGRAM);sender.sendto(b'bad',('127.0.0.1',udp));until(lambda:c.call('/api/state')['received']>0,'UDP received');received=c.call('/api/state')['received'];c.call('/api/control',dict(action='stop'));s=c.call('/api/configuration/schedule');check(not s['pending'] and not s['monitoring'] and c.call('/api/config')['FramePeriodMs']==322 and c.call('/api/decoders')['configuration']['Packets'][0]['Name']=='停止时生效的新包名','stopping reception flushes all pending settings before the stop response');check(c.call('/api/state')['received']==received,'immediate application on stop preserves reception counters')
   c.call('/api/control',dict(action='start'));post_settings(FramePeriodMs=323);time.sleep(1.2);check(c.call('/api/config')['FramePeriodMs']==322,'the background check does not apply pending settings early while reception is running');tick();until(lambda:not c.call('/api/configuration/schedule')['pending'],'running tick');check(c.call('/api/config')['FramePeriodMs']==323 and c.call('/api/state')['mode']=='live','running pending settings apply through the periodic batch without stopping reception')
   post_settings(FramePeriodMs=324);c.call('/api/send-monitor/control',dict(action='stop'));check(c.call('/api/config')['FramePeriodMs']==324 and not c.call('/api/configuration/schedule')['pending'],'the older combined stop-control route also flushes pending changes immediately')
   c.call('/api/control',dict(action='start'));post_settings(FramePeriodMs=325);stop_process();start();check(c.call('/api/config')['FramePeriodMs']==325 and not c.call('/api/configuration/schedule')['pending'] and c.call('/api/state')['mode']=='stopped','pending settings recovered after restart apply immediately while reception is stopped');sender.close()
   (out/'timing-report.json').write_text(json.dumps(dict(checks=len(checks),details=checks),ensure_ascii=False,indent=2),encoding='utf-8');print('All',len(checks),'configuration timing checks passed.')
  finally:stop_process()

if __name__=='__main__':main()
