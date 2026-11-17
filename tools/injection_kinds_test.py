"""YKE/YKM import and sequence-converter integration; isolated root, no commands sent."""
import csv,io,json,secrets,shutil,subprocess,sys,tempfile,urllib.parse,urllib.request
from pathlib import Path
from integration_test import ROOT,Client,free_port,wait,seed_send_monitor
from sequence_test import xlsx

def main():
 exe=Path(sys.argv[1]).resolve();checks=[];out=ROOT/'test-output/release-v0.9.8'
 def check(ok,label):assert ok,label;checks.append(label);print('PASS',label,flush=True)
 with tempfile.TemporaryDirectory(dir=ROOT/'test-output',prefix='injection-kinds-') as scratch:
  root=Path(scratch);shutil.copytree(ROOT/'config',root/'config');shutil.copytree(ROOT/'web',root/'web');seed_send_monitor(root)
  for name in ['decoder.json','continuous-rules.json','tc-bindings.json','observed-commands.json','sequences.json']:(root/'config'/name).unlink(missing_ok=True)
  port=free_port();c=Client(port);password=secrets.token_urlsafe(16);proc=None
  def start():
   nonlocal proc
   proc=subprocess.Popen([str(exe),'--local','--root',str(root),'--web-port',str(port)],stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL,creationflags=subprocess.CREATE_NO_WINDOW)
   def ready():
    try:return c.call('/api/auth/me')
    except OSError:return False
   wait(ready,'test server')
  def binary(path,raw,query):
   req=urllib.request.Request(c.base+path+'?'+urllib.parse.urlencode(query),data=raw,headers={'Content-Type':'application/octet-stream','X-CSRF-Token':c.csrf})
   return json.load(c.opener.open(req,timeout=15))
  def preview(raw=b'\x1b\x08\x00\x00\x01\x02',**kw):return binary('/api/send-monitor/commands/preview',raw,dict(format='injection-bin',filename='ordinary.bin',**kw))
  def apply(value):assert value['canApply'],value;c.call('/api/send-monitor/commands/apply',dict(token=value['token']))
  def catalog():return c.call('/api/send-monitor/commands/list?kind=YKM')
  try:
   start();c.call('/api/auth/setup',dict(username='injectiontypes',name='注入类别验证',password=password));c.csrf=c.call('/api/auth/login',dict(username='injectiontypes',password=password))['csrf'];path=root/'config/observed-commands.json'
   p=preview();check(p['canApply'] and p['rows'][0]['code']=='YKE0001','existing clients default to independent YKE numbering');apply(p)
   p=preview(kind='YKM');check(p['canApply'] and p['rows'][0]['code']=='YKM0001' and p['addedCommands']==0 and p['addedCategories']==1,'same binary adds a YKM category without duplicating its source or YKE code');apply(p)
   p=preview(kind='YKM',destinationIP='192.168.1.112');check(p['rows'][0]['code']=='YKM0001' and p['addedCategories']==0,'repeat YKM at another destination retains its number');apply(p)
   p=preview(b'\x10\x20',kind='YKM',code='ykm0021',name='手工名称');check(p['canApply'] and p['rows'][0]['code']=='YKM0021','manual YKM number accepts lowercase and preserves custom name');apply(p)
   p=preview(b'\x10\x21',kind='YKM');check(p['rows'][0]['code']=='YKM0022','YKM automatic counter follows its own manually assigned number');apply(p)
   p=preview(b'\x10\x22');check(p['rows'][0]['code']=='YKE0002','YKM numbering never consumes the YKE counter')
   p=binary('/api/send-monitor/commands/preview',b'\x10\x23',dict(format='injection-bin',filename='前缀_ykm0030-注入名称.bin'));check(p['canApply'] and p['rows'][0]['code']=='YKM0030' and p['rows'][0]['name']=='注入名称','YKM filename supplies category, code and name');apply(p)
   before=path.read_bytes()
   for code in ['YKM0000','YKM021','YKM0021','YKE0005']:
    p=preview(b'\x99\x98',kind='YKM',code=code);check(not p['canApply'] and path.read_bytes()==before,'invalid, occupied or wrong-category code cannot alter configuration: '+code)
   rows=catalog()['rows'];check([r['code'] for r in rows]==['YKM0001','YKM0021','YKM0022','YKM0030'],'YKM filter uses numeric code order')
   data=c.opener.open(c.base+'/api/send-monitor/commands/export.csv').read();exported=list(csv.reader(io.StringIO(data.decode('utf-8-sig'))));check(len(exported)==6 and {r[2] for r in exported[1:]}=={'YKE','YKM'},'CSV exports both injection categories in the existing six-column format')
   p=binary('/api/send-monitor/commands/preview',data,{});check(p['canApply'] and p['addedCommands']==p['addedCategories']==0,'mixed YKE/YKM CSV round trip is lossless');apply(p)
   command_rows=[['','known','TC.YKM0030'],['','unknown E','YKE9999'],['TEXT','unknown M','utc.ykm9999'],['','known in description YKM0030','YKM8888'],['','mixed','YKM0030 / YKE7777'],['','boundary','NOTYKM9999X'],['','description only YKM9998',''],['','telemetry','TME.YKM9999.value'],['','known lower','ykm0030'],['','multiple missing','YKE6666 YKM6666'],['','bare prefix','YKE']]
   source=xlsx([('第一表',command_rows),('第二表',[['','second sheet','YKM9999']])]);(out/'injection-conversion.xlsx').write_bytes(source)
   p=binary('/api/sequence/preview',source,dict(filename='注入检查.xlsx',mode='judge',sourceIP='',timeout=30,convert='commands'));warnings=[r for r in p['conversion'] if r.get('warning')]
   expected={('第一表',3),('第一表',4),('第一表',5),('第一表',6),('第一表',11),('第一表',12),('第二表',2)}
   check({(r['sheet'],r['row']) for r in warnings}==expected,'converter warns on exact unknown YKE/YKM tokens, with original sheet and row; known codes and telemetry do not warn')
   check(all('第'+str(r['row'])+'行YKE/YKM指令未入库请确认指令代号' in r['reason'] for r in warnings),'every missing injection warning contains the requested text and original row')
   converted=p['definitions'][0]['definition']['Rows'];check(converted[0]['Command']=='YKM0030' and converted[8]['Command']=='YKM0030','known injection codes normalize including legacy prefixes and lowercase')
   check(converted[1]['Keyword']=='TEXT' and converted[2]['Keyword']=='TEXT' and converted[1]['Command']=='YKE9999','unknown injection commands retain their source and the established TEXT conversion')
   plain=binary('/api/sequence/preview',source,dict(filename='原始.xlsx',mode='judge',sourceIP='',timeout=30));check(not plain['conversion'] and plain['definitions'][0]['definition']['Rows'][1]['Keyword']=='','ordinary sequence reading is unchanged')
   check(path.read_bytes()==before and c.call('/api/sequence/config')['definitions']==[],'conversion leaves the command library and saved sequences unchanged')
   proc.terminate();proc.wait(8);start();c.csrf=c.call('/api/auth/login',dict(username='injectiontypes',password=password))['csrf'];check([r['code'] for r in catalog()['rows']]==['YKM0001','YKM0021','YKM0022','YKM0030'],'YKM categories, destinations and counters survive restart')
   p=preview(b'\x22\x33',kind='YKM');check(p['rows'][0]['code']=='YKM0031','YKM numbering continues correctly after restart')
   (out/'injection-kinds-report.json').write_text(json.dumps(dict(checks=len(checks),details=checks),ensure_ascii=False,indent=2),encoding='utf-8');print('All',len(checks),'YKE/YKM integration checks passed.')
  finally:
   if proc:proc.terminate();proc.wait(8)

if __name__=='__main__':main()
