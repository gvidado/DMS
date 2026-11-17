"""Legacy command conversion, ambiguity handling and literal multi-sheet XLSX round trip."""
import sys,json,tempfile,shutil,subprocess,secrets,urllib.request,urllib.parse,io,zipfile
from pathlib import Path
from integration_test import ROOT,Client,free_port,wait,seed_send_monitor
from sequence_test import xlsx

def main():
    exe=Path(sys.argv[1]).resolve();checks=[]
    def check(ok,label):assert ok,label;checks.append(label);print('PASS',label,flush=True)
    with tempfile.TemporaryDirectory(dir=ROOT/'test-output',prefix='sequence-conversion-') as scratch:
        root=Path(scratch);shutil.copytree(ROOT/'config',root/'config');shutil.copytree(ROOT/'web',root/'web');seed_send_monitor(root)
        for name in ['decoder.json','continuous-rules.json','tc-bindings.json','observed-commands.json','sequences.json']:(root/'config'/name).unlink(missing_ok=True)
        commands=[dict(Number=i,Name=name,DestinationIP=ip,PayloadHex=f'AA {i:02X}',Length=2,Categories=[dict(Kind=kind,Code=code,DestinationIPs=[ip])]) for i,name,ip,kind,code in [(1,'点火','192.168.1.113','BC','BC2133'),(2,'关机','192.168.1.111','PSS','PSS001'),(3,'点火','192.168.1.137','KC','KC0001'),(4,'另一令','192.168.1.113','BC','BC21330'),(5,'示例令','192.168.1.113','BC','BC0001'),(6,'KC','192.168.1.137','KC','KC0002'),(7,'PSS','192.168.1.111','PSS','PSS002'),(8,'a1','192.168.1.113','BC','BC0003')]]
        (root/'config/observed-commands.json').write_text(json.dumps(dict(SchemaVersion=1,SourceIP='127.0.0.1',NextNumber=5,IdentityRule='udp-payload',Commands=commands)),encoding='utf-8');port=free_port();c=Client(port);password=secrets.token_urlsafe(16)
        process=subprocess.Popen([str(exe),'--local','--root',str(root),'--web-port',str(port)],stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL,creationflags=subprocess.CREATE_NO_WINDOW)
        try:
            def ready():
                try:c.call('/api/auth/me');return True
                except OSError:return False
            wait(ready,'conversion server');c.call('/api/auth/setup',dict(username='convertqa',name='QA',password=password));c.csrf=c.call('/api/auth/login',dict(username='convertqa',password=password))['csrf']
            rows=[['','旧写法','KUTC.YBC2133','','','',''],['','发送 BC2133','','','','',''],['','发送pss001','','','','',''],['','已有','BC2133','','','',''],['','模糊','点火','','','',''],['','发送 BC2133 和 PSS001','','','','',''],['','未知','KUTC.YBC9999','','','',''],['','不碰参数','TM.BC2133.value','','','',''],['','边界','KUTC.YBC21330','','','',''],['','前缀边界','BC213300','','','',''],['','唯一名称','关机','','','',''],['TEXT','=1+2','','','','',''],['','说明发送 BC2133','UNKNOWN','','','','']]
            rows.extend([['','用户有库例子','UTC.YBC0001','','','',''],['','用户无库例子','UCT.YBC6001','','','','']]);rows.extend([['TEXT','库内指令','UTC.YBC0001','','','',''],[' tExT ','预置 BC2133','','','','',''],['TEXT','仍是陌生指令','UCT.YBC6002','','','',''],['TEXT','说明 BC0001','UNKNOWN','','','',''],['HOLD','保留非TEXT关键字','BC0001','','','',''],['TEXT','多个候选','点火','','','','']]);rows.extend([['','陌生甲','COMD.KC9999'],['','陌生乙','SETP.PSS9999'],['','已知KC','COMD.KC0001'],['','已知PSS','SETP.PSS001'],['','嵌入说明','COMD.KC9998','','','',''],['','参数a10说明','SETP.PSS9998'],['TEXT','发送 BC0001','COMD.KC9997']]);raw=xlsx([('转换',rows),('第二张',[['','发送PSS001','','','','','']])])
            def upload(data,convert=True):
                query=urllib.parse.urlencode(dict(filename='转换.xlsx',mode='judge',timeout='30',convert='commands' if convert else ''))
                return json.load(c.opener.open(urllib.request.Request(c.base+'/api/sequence/preview?'+query,data=data,headers={'Content-Type':'application/octet-stream','X-CSRF-Token':c.csrf})))
            before=(root/'config/observed-commands.json').read_bytes();plain=upload(raw,False);check(plain['definitions'][0]['definition']['Rows'][0]['Command']=='KUTC.YBC2133','ordinary read never converts legacy syntax')
            value=upload(raw);out=value['definitions'][0]['definition']['Rows'];get=lambda i:out[i-1]['Command']
            check(get(1)=='BC2133' and get(2)=='BC2133' and get(3)=='PSS001','legacy prefix and description-based BC/PSS commands convert to canonical catalog codes')
            check(get(4)=='BC2133' and out[3]['Description'].count('（BC2133 点火）')==1,'existing code is unchanged and command annotation is not duplicated')
            check(get(5)=='点火' and get(6)=='' and any('多个匹配' in r['reason'] for r in value['conversion']),'name-only content stays literal while multiple actual codes still list candidates')
            check(get(7)=='KUTC.YBC9999' and get(8)=='TM.BC2133.value' and get(10)=='BC213300','unknown commands, TM references and longer nonmatching codes stay unchanged')
            check(get(9)=='BC21330' and get(11)=='关机' and out[10]['Keyword']=='TEXT','numeric code boundaries are preserved and a unique command name alone becomes TEXT')
            check(get(13)=='UNKNOWN','description inference runs only for an empty command column')
            check(all(out[i]['Keyword']=='TEXT' for i in [4,6,9,10,14,20]) and all(out[i]['Keyword']=='' for i in [5,7]),'unmatched codes and name-only instructions become TEXT while multiple codes and TM cells keep their keyword')
            check(all(not any(('第'+str(i+2)+'行：') in error for error in value['definitions'][0]['errors']) for i in [6,9,14]) and len([r for r in value['conversion'] if r['status']=='已设为TEXT'])==11,'unknown-command annotation rows no longer cause missing-command validation errors and appear in the report')
            check(get(14)=='BC0001' and out[13]['Keyword']=='','UTC.YBC0001 searches its contained code first and converts to the existing BC0001 rather than TEXT')
            check(get(15)=='UCT.YBC6001' and out[14]['Keyword']=='TEXT','UCT.YBC6001 becomes TEXT only after its contained BC6001 fails catalog lookup')
            check(out[15]['Keyword']=='' and get(16)=='BC0001','known legacy command in command column clears TEXT and normalizes its code')
            check(out[16]['Keyword']=='' and get(17)=='BC2133','known command in description clears case-insensitive TEXT and fills an empty command column without requiring the send verb')
            check(out[17]['Keyword']=='TEXT' and get(18)=='UCT.YBC6002','unknown commands still remain TEXT')
            check(out[18]['Keyword']=='' and get(19)=='UNKNOWN' and any(r['row']==20 and '人工确认' in r['reason'] for r in value['conversion']),'description match clears TEXT but conflicting unknown command text remains for manual confirmation')
            check(out[19]['Keyword']=='HOLD' and out[20]['Keyword']=='TEXT','known code preserves a non-TEXT keyword while shared names alone remain TEXT')
            check(out[21]['Keyword']=='TEXT' and get(22)=='COMD.KC9999' and out[22]['Keyword']=='TEXT' and get(23)=='SETP.PSS9999','unknown COMD.KC and SETP.PSS cannot match names embedded inside their reference')
            check(get(24)=='KC0001' and get(25)=='PSS001' and out[23]['Keyword']=='' and out[24]['Keyword']=='','known COMD.KC and SETP.PSS still normalize to catalog codes')
            check(out[25]['Keyword']=='TEXT' and out[26]['Keyword']=='TEXT','short ASCII command names do not match inside unknown references or larger description identifiers')
            check(out[27]['Keyword']=='' and get(28)=='COMD.KC9997','a known code in description still clears TEXT and preserves conflicting command for review')
            check(value['definitions'][1]['definition']['Rows'][0]['Command']=='PSS001','each worksheet converts independently')
            request=urllib.request.Request(c.base+'/api/sequence/export-converted',data=json.dumps(dict(token=value['token'])).encode(),headers={'Content-Type':'application/json','X-CSRF-Token':c.csrf});export=c.opener.open(request).read()
            with zipfile.ZipFile(io.BytesIO(export)) as z:check(b'<f>' not in z.read('xl/worksheets/sheet1.xml') and len([n for n in z.namelist() if n.startswith('xl/worksheets/')])==2,'export is a real multi-sheet XLSX with literal strings rather than executable Excel formulas')
            again=upload(export,False);check(again['definitions'][0]['definition']['Rows']==out,'exported seven-column workbook re-imports without losing row values or numbers')
            only_codes=upload(xlsx([('仅代号',[['','仅名称','关机'],['TEXT','已有注释名称','关机'],['','发送关机'],['TEXT','关机'],['TEXT','说明关机','COMD.KC9999'],['TEXT','发送 PSS001','关机'],['TEXT','说明只有名称关机','BC2133'],['','包含完整代号','UTC.YBC0001'],['','编号边界','SETP.PSS0010']])]))
            code_rows=only_codes['definitions'][0]['definition']['Rows']
            check(all(code_rows[i]['Keyword']=='TEXT' for i in [0,1,2,3,4,8]) and code_rows[0]['Command']=='关机' and code_rows[2]['Command']=='','unique names and send-description names never replace codes or clear TEXT')
            check(code_rows[5]['Keyword']=='' and code_rows[5]['Command']=='关机' and any(r['row']==7 and '人工确认' in r['reason'] for r in only_codes['conversion']),'a real code in description qualifies but preserves conflicting command text for review')
            check(code_rows[6]['Keyword']=='' and code_rows[6]['Command']=='BC2133' and code_rows[7]['Command']=='BC0001','a real code in the command column qualifies regardless of a name in description and supports legacy prefixes')
            descriptions=upload(xlsx([('说明直接提取',[
                ['TEXT','BC2133'],['','bc2133'],['','检查BC2133后的参数状态'],['TEXT','核对 UTC.YBC0001 后状态'],
                ['','PSS001'],['','KC0001'],['','BC2133与PSS001'],['TEXT','BC213300'],['TEXT','点火'],
                ['TEXT','说明BC2133','BC0001']])]))
            direct=descriptions['definitions'][0]['definition']['Rows']
            check([r['Command'] for r in direct[:6]]==['BC2133','BC2133','BC2133','BC0001','PSS001','KC0001'] and all(r['Keyword']=='' for r in direct[:6]),'bare, lowercase, embedded and legacy-prefixed catalog codes fill command cells without any send verb')
            check(all('无需包含“发送”字样' in r['reason'] for r in descriptions['conversion'][:6]),'conversion report explains description-only code extraction')
            check(direct[6]['Command']=='' and any(r['row']==8 and '多个匹配' in r['reason'] for r in descriptions['conversion']),'multiple description codes remain candidates for manual selection without an arbitrary fill')
            check(direct[7]['Command']=='' and direct[8]['Command']=='' and direct[7]['Keyword']=='TEXT' and direct[8]['Keyword']=='TEXT','longer unknown identifiers and command names alone do not qualify as catalog codes')
            check(direct[9]['Command']=='BC0001','description extraction never overwrites an already filled command cell')
            direct_export=c.opener.open(urllib.request.Request(c.base+'/api/sequence/export-converted',data=json.dumps(dict(token=descriptions['token'])).encode(),headers={'Content-Type':'application/json','X-CSRF-Token':c.csrf})).read()
            check(upload(direct_export,False)['definitions'][0]['definition']['Rows']==direct,'bare-code inference survives Excel export and ordinary re-import')
            marker_sheets=[]
            for col in [0,2,3,5,6]:
                r=['','建立起点','','','','',''];r[col]=' t0 ';marker_sheets.append(('错列'+str(col),[r,['','后续','BC0001','','','T0:.2','']]))
            markers=upload(xlsx(marker_sheets))
            for item in markers['definitions']:
                r=item['definition']['Rows'][0]
                check(r['Keyword']=='TEXT' and r['Mark']=='T0' and all(r[field]=='' for field in ['Command','Condition','Expected','Timeout']) and not item['errors'],'standalone T0 moves to marker without column shifts: '+item['definition']['Sheet'])
            check(all(r['markAfter']=='T0' and r['markBefore']=='' for r in markers['conversion'] if r['status']=='已移至标志列'),'conversion report records the marker column change')
            special=upload(xlsx([('动作同行',[['T0','建立并发令','BC0001','','','F:2','3'],['','后续','PSS001','','','T0:.2','']]),('已有标志',[['T0','重复位置','','','T0']]),('冲突',[['T0','保留冲突','','','OTHER']]),('时间表达式',[['TEXT','起点','','','T0'],['','时间不移动','BC0001','','','T0:2','']]),('说明文字',[['TEXT','说明中的T0不搬动']])]))
            items=special['definitions'];r=items[0]['definition']['Rows'][0]
            check(r['Keyword']=='' and r['Command']=='BC0001' and r['Mark']=='T0' and r['Expected']=='F:2' and r['Timeout']=='3' and not items[0]['errors'],'moving a keyword T0 preserves the command and scheduling fields')
            check(items[1]['definition']['Rows'][0]['Keyword']=='TEXT' and not items[1]['errors'],'already matching marker is deduplicated on the same row')
            check(items[2]['definition']['Rows'][0]['Keyword']=='T0' and items[2]['definition']['Rows'][0]['Mark']=='OTHER' and bool(items[2]['errors']) and any(r['status']=='T0位置冲突' for r in special['conversion']),'a conflicting existing marker is retained and reported for manual correction')
            check(items[3]['definition']['Rows'][1]['Expected']=='T0:2' and items[3]['definition']['Rows'][1]['Mark']=='' and not items[3]['errors'] and items[4]['definition']['Rows'][0]['Mark']=='','T0:n stays in expected time and descriptive T0 is not moved')
            markers=upload(xlsx(marker_sheets))
            marker_export=c.opener.open(urllib.request.Request(c.base+'/api/sequence/export-converted',data=json.dumps(dict(token=markers['token'])).encode(),headers={'Content-Type':'application/json','X-CSRF-Token':c.csrf})).read()
            with zipfile.ZipFile(io.BytesIO(marker_export)) as z:
                from xml.etree import ElementTree as ET
                ns={'s':'http://schemas.openxmlformats.org/spreadsheetml/2006/main'};sheet=ET.fromstring(z.read('xl/worksheets/sheet1.xml'));cells={e.attrib['r']:''.join(t.text or '' for t in e.findall('.//s:t',ns)) for e in sheet.findall('.//s:row[@r="2"]/s:c',ns)}
                check(cells['A2']=='TEXT' and cells['E2']=='T0' and cells['C2']=='' and cells['F2']=='','export places T0 in Excel column E (marker), not keyword/command/expected')
            marker_again=upload(marker_export,False)
            check([x['definition']['Rows'] for x in marker_again['definitions']]==[x['definition']['Rows'] for x in markers['definitions']],'converted T0 columns survive export and ordinary re-import')
            plain_marker=upload(xlsx([('普通读取',[['T0','不自动转换']])]),False)
            check(plain_marker['definitions'][0]['definition']['Rows'][0]['Keyword']=='T0','ordinary read does not silently move original cells')
            check((root/'config/observed-commands.json').read_bytes()==before and c.call('/api/sequence/config')['definitions']==[],'conversion and export leave the command library and saved sequences unchanged')
        finally:process.terminate();process.wait(timeout=5)
    print('All',len(checks),'sequence conversion checks passed. No commands sent.')
if __name__=='__main__':main()
