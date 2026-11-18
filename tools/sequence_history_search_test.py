"""Search run metadata, then retrieve/export complete runs, including old and repeated names."""
import sys,json,shutil,tempfile,subprocess,secrets,urllib.parse,csv,io
from pathlib import Path
from integration_test import ROOT,Client,free_port,wait

EXPORT_COLUMNS=['模式','工作名称','原始行号','事件','实际时间','状态','测试工作内容及要求','参数实际值','指令名称','实际开始序列秒','异常原因']

def fixture_events(count=63):
    events=[]
    for number in range(1,count+1):
        run=f'{number:032x}';name='同名试验' if number<=2 else '旧版试验' if number==3 else '其他试验'+str(number);code='XLP0001' if number<=2 else '' if number==3 else 'XLP'+str(number).zfill(4)
        for kind in ['开始','步骤结果','结束']:
            item=dict(Event=kind,RunId=run,Mode='judge',Sequence=name,Code=code,Time=f'2026-11-01 08:{number//60:02}:{number%60:02}.000',Status='运行中' if kind=='开始' else '通过' if kind=='步骤结果' else '全部通过',Row=2 if kind=='步骤结果' else 0)
            if kind=='步骤结果':item.update(Input=dict(Row=2,Keyword='TEXT',Description='完整步骤，含"引号"\n第二行'),Values='{"TME.A1.value":"9.79","TMB.A2.value":"18446744073709551615"}',CommandName='测试指令,"一"',Actual=12.345678);item.pop('Sequence');item.pop('Code')
            events.append(item)
    return events

def main():
    exe=Path(sys.argv[1]).resolve();checks=[]
    def check(ok,label):assert ok,label;checks.append(label);print('PASS',label,flush=True)
    with tempfile.TemporaryDirectory(dir=ROOT/'test-output',prefix='sequence-run-search-') as scratch:
        root=Path(scratch);shutil.copytree(ROOT/'config',root/'config');shutil.copytree(ROOT/'web',root/'web');(root/'data').mkdir()
        path=root/'data/sequence-history.jsonl';path.write_text(''.join(json.dumps(e,ensure_ascii=False)+'\n' for e in fixture_events()),encoding='utf-8');port=free_port();password=secrets.token_urlsafe(16);process=None;c=Client(port)
        def start():
            nonlocal process,c
            process=subprocess.Popen([str(exe),'--local','--root',str(root),'--web-port',str(port)],stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL,creationflags=subprocess.CREATE_NO_WINDOW);c=Client(port)
            def ready():
                try:c.call('/api/auth/me');return True
                except OSError:return False
            wait(ready,'run search server')
        def query(route,**fields):return c.call('/api/sequence/'+route+'?'+urllib.parse.urlencode(fields))
        def exported(**fields):return list(csv.DictReader(io.StringIO(c.opener.open(c.base+'/api/sequence/export.csv?'+urllib.parse.urlencode(fields)).read().decode('utf-8-sig'))))
        try:
            start();c.call('/api/sequence/runs',expected=401);c.call('/api/auth/setup',dict(username='searchqa',name='QA',password=password));c.csrf=c.call('/api/auth/login',dict(username='searchqa',password=password))['csrf']
            runs=query('runs',page=1);check(runs['total']==63 and runs['pages']==2 and len(runs['rows'])==50,'run list pages all historical runs rather than only current or recent events')
            check(query('runs',page=2)['rows'][-1]['RunId']==f'{1:032x}','oldest run remains selectable through run-list pagination')
            unchanged=query('runs',page=1,since=runs['revision'])
            check(unchanged.get('unchanged') and 'rows' not in unchanged,
                  'unchanged run list sends a revision instead of the same page of rows')
            same=query('runs',search='同名试验');check(same['total']==2 and len({r['RunId'] for r in same['rows']})==2 and same['rows'][0]['StartedAt']!=same['rows'][1]['StartedAt'],'same-name repeated runs remain distinct with timestamps and unique run IDs')
            check(query('runs',search='xlp0001')['total']==2,'code lookup is case-insensitive and returns each execution of the code')
            check(query('history',search='同名试验')['total']==6 and len(exported(search='同名试验'))==6,'name filtering includes all events of matching runs even when individual events lack a name/code')
            run=same['rows'][0]['RunId'];history=query('history',search='XLP0001',runId=run);check(history['total']==3 and all(e['RunId']==run for e in history['rows']),'selecting an individual run filters its complete step and lifecycle history')
            same_query = query('history', runId=run, search='XLP0001', since=history['revision'])
            check(same_query.get('unchanged') and 'rows' not in same_query,'unchanged history returns a revision without resending rows')
            csvrows=exported(search='XLP0001',runId=run);check(len(csvrows)==3 and all(e['实际时间']==same['rows'][0]['StartedAt'] for e in csvrows),'CSV export follows the selected run and query filter')
            raw=c.opener.open(c.base+'/api/sequence/export.csv?'+urllib.parse.urlencode(dict(runId=run))).read();table=list(csv.reader(io.StringIO(raw.decode('utf-8-sig'))))
            check(raw.startswith(b'\xef\xbb\xbf') and table[0]==EXPORT_COLUMNS and all(len(r)==11 for r in table),'CSV contains exactly the eleven requested columns in order, with Excel-compatible UTF-8 BOM')
            step=next(e for e in csvrows if e['事件']=='步骤结果');check(step['工作名称']=='同名试验' and step['模式']=='序列判读' and step['原始行号']=='2' and step['状态']=='通过','confirmed work name is filled for legacy steps that only retained a run ID')
            check(step['测试工作内容及要求']=='完整步骤，含"引号"\n第二行' and step['指令名称']=='测试指令,"一"' and step['实际开始序列秒']=='12.345678' and json.loads(step['参数实际值'])['TMB.A2.value']=='18446744073709551615','descriptions, quotes, newlines, command names, actual start seconds and exact parameter values map to the requested columns')
            archives=c.call('/api/judgment-archives?type=sequence-judge&name='+urllib.parse.quote('同名试验'))['history']['rows'];archive_id=next(a['id'] for a in archives if a['runId']==run)
            archived=list(csv.reader(io.StringIO(c.opener.open(c.base+'/api/judgment-archives/export.csv?'+urllib.parse.urlencode(dict(id=archive_id))).read().decode('utf-8-sig'))))
            check(archived==table,'per-run archive export uses the same eleven columns and values')
            check(query('history',runId=run,search='不存在')['total']==0 and exported(runId=run,search='不存在')==[],'query and run constraints combine consistently, including no matches')
            check(query('runs',search='旧版')['total']==1 and query('history',search='旧版')['total']==3,'legacy records without sequence codes remain searchable by name')
            with path.open('a',encoding='utf-8') as stream:
                for event in fixture_events(1):event['RunId']='f'*32;event['Sequence']='新追加运行';event['Mode']='execute';event['Code']='XLZ0001';stream.write(json.dumps(event,ensure_ascii=False)+'\n')
            check(query('history',runId=run,search='XLP0001',since=history['revision']).get('unchanged'),
                  'appending a separate run does not redraw the selected run')
            check(query('runs',search='新追加运行')['total']==1 and query('runs')['total']==64,'run index refreshes when the history file grows')
            check(len(exported(mode='execute'))==3 and all(e['模式']=='序列执行' and e['工作名称']=='新追加运行' for e in exported(mode='execute')),'execution export preserves mode filtering in the concise layout')
            c.call('/api/sequence/runs?search='+('a'*513),expected=400);check(True,'oversized searches return a clear validation error')
            process.terminate();process.wait(timeout=5);start();c.csrf=c.call('/api/auth/login',dict(username='searchqa',password=password))['csrf'];check(query('runs',search='同名试验')['total']==2 and query('history',runId=run)['total']==3,'historical run selection remains available after restart')
        finally:
            if process and process.poll() is None:process.terminate();process.wait(timeout=5)
    print('All',len(checks),'run-search checks passed. No sequence executed.')
if __name__=='__main__':main()
