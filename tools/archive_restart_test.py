"""Original 0.8.21 archive initialization across persisted clock boundaries; no UDP."""
import json,secrets,subprocess,sys,tempfile,zipfile,shutil
from pathlib import Path
from integration_test import ROOT,Client,free_port,wait

checks=[]
def check(ok,label):
 assert ok,label
 checks.append(label);print('PASS',label,flush=True)
with tempfile.TemporaryDirectory(prefix='archive-clock-v0821-',dir=ROOT/'test-output') as scratch:
 directory=Path(scratch).resolve();assert directory.is_relative_to((ROOT/'test-output').resolve())
 with zipfile.ZipFile(ROOT.parent/'离线包/自动判读系统_v0.8.21_离线包.zip') as z:
  for info in z.infolist():
   target=(directory/info.filename).resolve();assert target.is_relative_to(directory)
   if info.is_dir():target.mkdir(parents=True,exist_ok=True)
   else:target.parent.mkdir(parents=True,exist_ok=True);target.write_bytes(z.read(info))
 root=directory/'自动判读系统';exe=root/'TelemetryMonitor.exe';assert exe.exists()
 if '--exe' in sys.argv:shutil.copyfile(Path(sys.argv[sys.argv.index('--exe')+1]).resolve(),exe)
 data=root/'data';data.mkdir(exist_ok=True);hist=data/'sequence-history.jsonl';events=[]
 def sequence(n,stamp,step=None,finish=None,folder=None):
  run=f'{n:032x}';rows=[]
  for kind,time in [('开始',stamp),('步骤结果',step),('结束',finish)]:
   if time is None:continue
   e=dict(Event=kind,RunId=run,Code='XLP0001',Mode='judge',Sequence='time scenario '+str(n),Sheet='sheet',Operator='offline test',Time=time,Status='通过' if kind=='步骤结果' else '全部通过' if kind=='结束' else '运行中',Reason='',Row=2 if kind=='步骤结果' else 0,Elapsed=1.25 if kind=='步骤结果' else 2.5 if kind=='结束' else 0)
   if kind=='步骤结果':e.update(Input=dict(Row=2,Keyword='',Description='跨日/校时记录',Command='',Condition='abs(TM.A1.value - 9.8) < 1',Mark='',Expected='',Timeout='30'),Values='{"TM.A1.value":9.79}',Actual=.2,ShipTime=dict(Value='12345',Status='有效',SourceIP='127.0.0.1',SampleTime=time))
   rows.append(e)
  events.extend(rows);path=data/'archives/sequences'/(folder or stamp[:10])/(run+'.jsonl');path.parent.mkdir(parents=True,exist_ok=True);path.write_text(''.join(json.dumps(e,ensure_ascii=False)+'\n' for e in rows),encoding='utf-8');return path
 cross=sequence(1,'2026-11-06 23:59:58.000','2026-11-07 00:00:01.000','2026-11-07 00:00:02.000')
 back=sequence(2,'2026-11-06 23:00:00.000','2026-11-06 22:00:01.000','2026-11-06 22:00:02.000')
 forward=sequence(3,'2026-11-06 23:59:58.000','2026-11-08 01:00:01.000','2026-11-08 01:00:02.000')
 unfinished=sequence(4,'2026-11-06 23:50:00.000')
 same_day=sequence(5,'2026-11-07 09:00:00.000','2026-11-07 09:00:01.000','2026-11-07 09:00:02.000')
 hist.write_text(''.join(json.dumps(e,ensure_ascii=False)+'\n' for e in events),encoding='utf-8')
 originals={str(p):p.read_bytes() for p in [cross,back,forward,same_day]};port=free_port();c=Client(port);process=None;password=secrets.token_urlsafe(18)
 def start(first=False):
  global process,c
  process=subprocess.Popen([str(exe),'--local','--root',str(root),'--web-port',str(port)],stdout=subprocess.DEVNULL,stderr=subprocess.STDOUT,creationflags=subprocess.CREATE_NO_WINDOW);c=Client(port)
  def ready():
   try:c.call('/api/auth/me');return True
   except OSError:return False
  wait(ready,'0.8.21 startup')
  if first:c.call('/api/auth/setup',dict(username='clockqa',name='Offline clock test',password=password))
  c.csrf=c.call('/api/auth/login',dict(username='clockqa',password=password))['csrf']
  return c.call('/api/judgment-archives')
 def stop():
  global process
  process.terminate();process.wait(timeout=8);process=None
 try:
  state=start(True);check(not state['error'] and state['history']['total']==5,'loads same-day, previous-day, midnight-spanning, backward-clock and forward-clock sequence records')
  check(all(Path(p).read_bytes()==v for p,v in originals.items()),'completed records keep their original timestamps and archive bytes')
  raw=[json.loads(line) for line in hist.read_text(encoding='utf-8-sig').splitlines() if line];check(len([e for e in raw if e['RunId']==f'{4:032x}' and e['Event']=='结束'])==1,'unfinished prior-day sequence receives exactly one restart-termination event')
  ended=hist.read_bytes();stop();state=start()
  if state['error']:
   report=ROOT/'test-output/archive-time-v0821/repro';report.mkdir(parents=True,exist_ok=True);(report/'state.json').write_text(json.dumps(state,ensure_ascii=False,indent=2),encoding='utf-8');(report/'sequence-history.jsonl').write_bytes(hist.read_bytes());shutil.copyfile(exe,report/'TelemetryMonitor.v0821.exe')
   for path in (data/'archives/sequences').rglob('*.jsonl'):shutil.copyfile(path,report/path.name)
   print('RESTART_ERROR',state['error'],flush=True)
  check(not state['error'] and hist.read_bytes()==ended,'a second server-process restart remains consistent and does not duplicate termination')
  stop();rows=[json.loads(line) for line in cross.read_text(encoding='utf-8-sig').splitlines() if line];rows[1]['Status']='异常';cross.write_text(''.join(json.dumps(e,ensure_ascii=False)+'\n' for e in rows),encoding='utf-8');corrupt=cross.read_bytes();state=start();check('序列存档与原始历史不一致' in state['error'] and cross.read_bytes()==corrupt,'the exact reported error is reproduced by actual content divergence and both files remain preserved')
  stop();backup=data/'archives/sequences_backup_test';(data/'archives/sequences').rename(backup);state=start();check(not state['error'] and state['history']['total']==5 and (data/'archives/sequences').exists() and backup.exists(),'renaming only the sequence mirror rebuilds it from intact original history without deleting the backup')
  stop();state=start();check(not state['error'] and hist.read_bytes()==ended,'rebuilt archives survive another restart while original history stays unchanged')
  stop();cross.write_bytes(b'');state=start();check(not state['error'] and cross.stat().st_size>0 and hist.read_bytes()==ended,'an empty sequence mirror is reconstructed from intact history without replacing original data')
  restored=cross.read_bytes();stop();extra=dict(Event='结束',RunId=f'{1:032x}',Mode='judge',Sequence='unexpected tail',Time='2026-11-07 00:01:00.000',Status='终止')
  with cross.open('a',encoding='utf-8') as f:f.write(json.dumps(extra,ensure_ascii=False)+'\n')
  with_extra=cross.read_bytes();state=start();check('条数不一致' in state['error'] and cross.read_bytes()==with_extra and hist.read_bytes()==ended,'an extra archive event absent from original history is detected and preserved')
  stop();cross.write_bytes(restored);duplicate=data/'archives/sequences/2026-11-07'/cross.name;duplicate.parent.mkdir(exist_ok=True);shutil.copyfile(cross,duplicate)
  state=start();check('重复运行编号' in state['error'] and duplicate.exists() and cross.read_bytes()==restored,'duplicate run IDs in separate date folders are reported without silently choosing one')
  stop();duplicate.unlink();state=start();check(not state['error'] and hist.read_bytes()==ended,'recovering the synthetic duplicate leaves original history and other archives intact')
  stop();archive_only=sequence(6,'2026-10-31 09:00:00.000',finish='2026-10-31 09:01:00.000');only_before=archive_only.read_bytes();state=start();check(not state['error'] and state['history']['total']==6 and archive_only.read_bytes()==only_before and hist.read_bytes()==ended,'valid older archive-only runs remain readable and are not erased or inserted into original history')
  (ROOT/('test-output/archive-time-v0821/report-'+('current' if '--current' in sys.argv else 'fixed0821')+'.json')).write_text(json.dumps(dict(version='current' if '--current' in sys.argv else '0.8.21 hotfix',checks=len(checks),details=checks),ensure_ascii=False,indent=2),encoding='utf-8')
 finally:
  if process is not None:stop()
print('All',len(checks),'archive time and recovery checks passed; no UDP listener or sender started.')
