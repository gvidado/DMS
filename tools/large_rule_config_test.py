"""Regression for large decoder/rule files and generated HTTP responses; no device I/O."""
import csv,io,json,secrets,shutil,subprocess,sys,tempfile,time,urllib.request
from pathlib import Path
from integration_test import ROOT,Client,free_port
from runtime_benchmark import seed

def main():
 exe=Path(sys.argv[1]).resolve();checks=[]
 def check(ok,label):assert ok,label;checks.append(label);print('PASS',label,flush=True)
 def until(fn,label):
  end=time.monotonic()+60
  while time.monotonic()<end:
   try:
    result=fn()
    if result:return result
   except OSError:pass
   time.sleep(.1)
  raise AssertionError(label)
 with tempfile.TemporaryDirectory(dir=ROOT/'test-output',prefix='large-rules-') as scratch:
  root=Path(scratch);seed(root,per_packet=500,packets=1);shutil.copytree(ROOT/'web',root/'web')
  def load(name):return json.loads((root/'config'/name).read_text(encoding='utf-8-sig'))
  def save(name,value):(root/'config'/name).write_text(json.dumps(value,ensure_ascii=False),encoding='utf-8')
  decoder=load('decoder.json')
  for p in decoder['Catalog']['Parameters']:p['Enums']={'0':'大注释项'+('x'*17500)}
  save('decoder.json',decoder);rules=load('continuous-rules.json')
  for r in rules['Rules']:r.update(Enabled=False,Script='RET=Normal;'+(' '*17500))
  save('continuous-rules.json',rules)
  check(all(len((root/'config'/n).read_text(encoding='utf-8'))>8*1024*1024 for n in ['decoder.json','continuous-rules.json']),'both valid decoder and rule configurations exceed the old 8MiB character limit')
  web=free_port();c=Client(web);proc=None;password=secrets.token_urlsafe(18)
  def start():
   nonlocal proc
   proc=subprocess.Popen([str(exe),'--local','--root',str(root),'--web-port',str(web)],stdout=(root/'server.log').open('ab'),stderr=subprocess.STDOUT,creationflags=subprocess.CREATE_NO_WINDOW)
   until(lambda:c.call('/api/auth/me'),'server startup')
  def login():c.csrf=c.call('/api/auth/login',dict(username='largeqa',password=password))['csrf']
  def state():return c.call('/api/continuous-rules')
  def request_bytes(path):
   with c.opener.open(c.base+path,timeout=30) as response:return response.read()
  try:
   start();c.call('/api/auth/setup',dict(username='largeqa',name='大配置验证',password=password));login()
   params=request_bytes('/api/continuous-rules/parameters');check(len(params)>8*1024*1024 and len(json.loads(params))==500,'parameter binding selector loads all 500 fields from a response above 8MiB')
   data=request_bytes('/api/continuous-rules');current=json.loads(data);check(len(data)>8*1024*1024 and current['total']==500 and not current['storageError'] and all(not r['error'] for r in current['rows']),'rule page opens through the configuration editor and serializes its full response above 8MiB')
   exported=json.loads(request_bytes('/api/continuous-rules/export'));check(len(exported['Rules'])==500 and exported['Rules'][-1]['Script']==rules['Rules'][-1]['Script'],'JSON rule export preserves the final rule and complete script')
   rows=list(csv.DictReader(io.StringIO(request_bytes('/api/continuous-rules/export.csv').decode('utf-8-sig'))));check(len(rows)==500 and rows[-1]['判读规则']==rules['Rules'][-1]['Script'],'CSV export retains every rule and the complete large rule text')
   target=current['rows'][0]['rule'];c.call('/api/continuous-rules/toggle',dict(id=target['Id'],revision=current['revision'],enabled=True));updated=state();check(updated['enabled']==1 and updated['total']==500,'toggling one rule saves a large configuration without dropping other rules')
   target=next(r['rule'] for r in updated['rows'] if r['rule']['Id']==target['Id']);target['Description']='大配置修改后保留';c.call('/api/continuous-rules/save',dict(id=target['Id'],revision=updated['revision'],rule=target));modified=state();check(modified['rows'][0]['rule']['Description']=='大配置修改后保留','editing a single rule succeeds through staging and runtime adoption')
   invalid=dict(modified['rows'][0]['rule']);invalid['Script']='x'*20001;c.call('/api/continuous-rules/save',dict(id=invalid['Id'],revision=modified['revision'],rule=invalid),expected=400);check(state()['revision']==modified['revision'],'the existing single-rule length check still rejects invalid rules atomically')
   c.call('/api/continuous-rules/toggle',dict(id=target['Id'],revision=modified['revision'],enabled=False,padding='x'*(129*1024)),expected=400);check(state()['revision']==modified['revision'],'HTTP request-size limits remain enforced independently of JSON storage/response capacity')
   proc.terminate();proc.wait(10);start();login();restored=state();check(restored['total']==500 and restored['enabled']==1 and restored['rows'][0]['rule']['Description']=='大配置修改后保留' and not restored['storageError'],'restart reloads the saved configuration above 8MiB without resetting rules')
   if len(sys.argv)>2:
    node=Path(sys.executable).parents[1]/'node/bin/node.exe'
    result=subprocess.run([str(node),str(ROOT/'tools/large_rules_browser.mjs'),sys.argv[2]],input=json.dumps(dict(base=c.base,username='largeqa',password=password,output=str(ROOT/'test-output/release-v0.10.2'))),capture_output=True,text=True,encoding='utf-8',timeout=120,creationflags=subprocess.CREATE_NO_WINDOW)
    print(result.stdout,flush=True)
    if result.returncode:raise AssertionError(result.stderr[-4000:])
   (ROOT/'test-output/release-v0.10.2/large-rules-report.json').write_text(json.dumps(dict(checks=len(checks),details=checks,parameters=500,ruleResponseBytes=len(data),parameterResponseBytes=len(params)),ensure_ascii=False,indent=2),encoding='utf-8')
  except Exception:
   print((root/'server.log').read_text(encoding='utf-8',errors='replace')[-3000:]);raise
  finally:
   if proc and proc.poll() is None:proc.terminate();proc.wait(10)

if __name__=='__main__':main()
