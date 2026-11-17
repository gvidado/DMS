"""Sequence row insertion/deletion, independent previews and live adoption in a local fixture."""
import copy,json,secrets,shutil,socket,subprocess,sys,tempfile,time,urllib.request
from pathlib import Path
from integration_test import ROOT,Client,free_port,seed_send_monitor
from sequence_edit_test import xlsx

def main():
 host=Path(sys.argv[1]).resolve();out=ROOT/'test-output/release-v0.9.5';out.mkdir(exist_ok=True);checks=[]
 (out/'rows-preview.xlsx').write_bytes(xlsx([('预览A',[['TEXT','原项'+str(i)] for i in range(52)]),('预览B',[['TEXT','另一表第一项'],['TEXT','另一表第二项']])]))
 def check(ok,label):assert ok,label;checks.append(label);print('PASS',label,flush=True)
 def until(fn,label):
  for _ in range(250):
   result=fn()
   if result:return result
   time.sleep(.04)
  raise AssertionError(label)
 with tempfile.TemporaryDirectory(dir=ROOT/'test-output',prefix='sequence-rows-') as scratch:
  root=Path(scratch);shutil.copytree(ROOT/'config',root/'config');shutil.copytree(ROOT/'web',root/'web');web=free_port();udp=free_port(socket.SOCK_DGRAM);source=free_port(socket.SOCK_DGRAM);c=Client(web);receiver=socket.socket(socket.AF_INET,socket.SOCK_DGRAM);receiver.bind(('127.0.0.2',0));receiver.settimeout(3)
  def save(name,value):(root/'config'/name).write_text(json.dumps(value),encoding='utf-8')
  for name in ['decoder.json','continuous-rules.json','tc-bindings.json','sequences.json','observed-commands.json']:(root/'config'/name).unlink(missing_ok=True)
  settings=json.loads((root/'config/server.json').read_text(encoding='utf-8-sig'));settings.update(BindAddress='127.0.0.1',UdpPort=udp,WebPort=web,SourceIPs=['127.0.0.1'],MulticastGroup='',MulticastInterface='');save('server.json',settings);seed_send_monitor(root)
  save('observed-commands.json',dict(SourceIP='127.0.0.1',NextNumber=2,Commands=[dict(Number=1,Name='仅发送一次',DestinationIP='127.0.0.2',DestinationIPs=['127.0.0.2'],PayloadHex='A1 01')]))
  save('tc-bindings.json',dict(Revision='tc-row-test',Bindings=[dict(Number=1,Code='K001')]));save('command-send.json',dict(SourceIP='127.0.0.1',Routes=[dict(DestinationIP='127.0.0.2',SourcePort=source,DestinationPort=receiver.getsockname()[1])]))
  definition=dict(Id='a'*32,Code='XLZ0001',Name='运行中增删',Mode='execute',SourceIP='127.0.0.1',Rows=[dict(Row=2,Keyword='',Description='已发指令',Command='K001',Expected='.2'),dict(Row=3,Keyword='TEXT',Description='等待项',Expected='F:3600')]);save('sequences.json',dict(Revision='row-test',Sequences=[definition],DefaultTimeoutSeconds=30,NextJudgeNumber=1,NextExecuteNumber=2))
  proc=subprocess.Popen([str(host),str(root),str(web)],stdin=subprocess.PIPE,stdout=subprocess.DEVNULL,stderr=subprocess.STDOUT,text=True,creationflags=subprocess.CREATE_NO_WINDOW)
  def ready():
   try:return c.call('/api/auth/me')
   except OSError:return False
  def tick():proc.stdin.write('tick\n');proc.stdin.flush();until(lambda:not c.call('/api/configuration/schedule')['pending'],'batch applied')
  def preview():return c.call('/api/sequence/edit-preview',dict(id=definition['Id'],revision=c.call('/api/sequence/config?draft=1')['revision']))
  def recheck(p,rows,next_number=None,expected=200):
   body=dict(token=p['token'],id=p['definitions'][0]['definition']['Id'],rows=rows)
   if next_number is not None:body['nextRowNumber']=next_number
   return c.call('/api/sequence/recheck',body,expected=expected)
  try:
   until(ready,'server ready');password=secrets.token_urlsafe(16);c.call('/api/auth/setup',dict(username='rowtest',name='行编辑测试',password=password));c.csrf=c.call('/api/auth/login',dict(username='rowtest',password=password))['csrf']
   raw=xlsx([('表A',[['TEXT','第一行'],['TEXT','第二行']]),('表B',[['TEXT','保持原样']])]);request=urllib.request.Request(c.base+'/api/sequence/preview?filename=rows.xlsx&mode=judge&timeout=30',data=raw,headers={'X-CSRF-Token':c.csrf,'Content-Type':'application/octet-stream'});p=json.loads(c.opener.open(request).read());other=copy.deepcopy(p['definitions'][1]);d=p['definitions'][0]['definition'];rows=d['Rows'];rows.insert(1,dict(Row=4,Keyword='TEXT',Description='中间插入'));rows.pop(2);checked=recheck(p,rows,5);d=checked['definitions'][0]['definition'];check([r['Row'] for r in d['Rows']]==[2,4] and d['Rows'][1]['Added'] and d['NextRowNumber']==5,'Excel preview supports inserted and deleted rows with stable original row numbers');check(checked['definitions'][1]==other,'editing one imported worksheet preserves the other worksheet')
   duplicate=copy.deepcopy(d['Rows']);duplicate.append(copy.deepcopy(duplicate[0]));recheck(checked,duplicate,expected=400);check(True,'duplicate step row identities are rejected without consuming the valid preview')
   empty=recheck(checked,[]);check(bool(empty['definitions'][0]['errors']),'deleting all rows remains editable but cannot pass validation');c.call('/api/sequence/apply',dict(token=empty['token'],ids=[d['Id']],mode='add'),expected=400);check(True,'an empty sequence cannot overwrite or enter the saved library')
   restored=recheck(empty,[dict(Row=5,Keyword='TEXT',Description='从空表添加')],6);check(not restored['definitions'][0]['errors'],'a new row can restore an empty preview');invalid=recheck(restored,[dict(Row=5,Keyword='PAR',Description='缺少CHANGE')],6);check(any('CHANGE' in e for e in invalid['definitions'][0]['errors']),'structural changes are revalidated for PAR and CHANGE boundaries');c.call('/api/sequence/preview/cancel',dict(token=invalid['token']))
   c.call('/api/control',dict(action='start'));cfg=c.call('/api/sequence/config');run=c.call('/api/sequence/control',dict(action='start',id=definition['Id'],revision=cfg['revision'],runName='增删切换任务',requestId=secrets.token_hex(16)))['runId'];assert receiver.recvfrom(100)[0]==bytes.fromhex('A101');until(lambda:c.call('/api/sequence/state?runId='+run)['rows'][0]['status']=='通过','first step complete');before=c.call('/api/sequence/state?runId='+run)['rows'][0];disk=(root/'config/sequences.json').read_bytes();p=preview();rows=p['definitions'][0]['definition']['Rows'];rows=[dict(Row=4,Keyword='TEXT',Description='插入在已发指令之前'),rows[0]];p=recheck(p,rows,5);check(not p['definitions'][0]['errors'] and (root/'config/sequences.json').read_bytes()==disk,'editing a saved sequence changes only its preview before saving');c.call('/api/sequence/save-edits',dict(token=p['token']));until(lambda:c.call('/api/configuration/schedule')['pending'],'pending save');check(c.call('/api/sequence/state?runId='+run)['runtime']['running'] and [r['Row'] for r in c.call('/api/sequence/definition?id='+definition['Id'])['definition']['Rows']]==[2,3],'row edits use the online pending configuration mechanism');tick();until(lambda:not c.call('/api/sequence/state?runId='+run)['runtime']['running'],'run completes on new definition');current=c.call('/api/sequence/state?runId='+run);check([r['input']['Row'] for r in current['rows']]==[4,2] and all(r['status']=='通过' for r in current['rows']),'the active task adopts inserted and deleted rows in their displayed order');check(current['rows'][1]==before and len(c.call('/api/command-send/history')['rows'])==1,'inserting before completed steps preserves their results and does not resend instructions')
   p=preview();rows=p['definitions'][0]['definition']['Rows'];p=recheck(p,[r for r in rows if r['Row']!=4]);c.call('/api/sequence/save-edits',dict(token=p['token']));tick();p=preview();rows=p['definitions'][0]['definition']['Rows'];recheck(p,rows+[dict(Row=3,Keyword='TEXT',Description='复用已删除编号')],expected=400);p=recheck(p,rows+[dict(Row=5,Keyword='TEXT',Description='下一新增编号')],6);check(p['definitions'][0]['definition']['NextRowNumber']==6 and p['definitions'][0]['definition']['Rows'][-1]['Added'],'deleted row identifiers are not recycled across saved edits');c.call('/api/sequence/save-edits',dict(token=p['token']));tick();c.call('/api/control',dict(action='stop'))
   proc.stdin.write('quit\n');proc.stdin.flush();proc.wait(8);proc=subprocess.Popen([str(host),str(root),str(web)],stdin=subprocess.PIPE,stdout=subprocess.DEVNULL,stderr=subprocess.STDOUT,text=True,creationflags=subprocess.CREATE_NO_WINDOW);until(ready,'restart');c.csrf=c.call('/api/auth/login',dict(username='rowtest',password=password))['csrf'];saved=c.call('/api/sequence/definition?id='+definition['Id'])['definition'];check(saved['NextRowNumber']==6 and [r['Row'] for r in saved['Rows']]==[2,5] and saved['Rows'][1]['Added'],'saved order, new-row metadata and identifier allocation survive restart')
   (out/'rows-report.json').write_text(json.dumps(dict(checks=len(checks),details=checks),ensure_ascii=False,indent=2),encoding='utf-8');print('All',len(checks),'sequence row checks passed.')
  finally:
   receiver.close()
   if proc.poll() is None:proc.terminate();proc.wait(8)

if __name__=='__main__':main()
