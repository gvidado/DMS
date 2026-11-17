"""Real workbook import through isolated authenticated HTTP; never sends commands."""
import csv,io,json,secrets,shutil,subprocess,sys,tempfile,urllib.request,zipfile
import xml.etree.ElementTree as ET
from pathlib import Path
from integration_test import ROOT,Client,free_port,wait,seed_send_monitor

def upload(c,raw,csrf=True,expected=200):
    req=urllib.request.Request(c.base+'/api/send-monitor/commands/preview?format=bc-xlsx',data=raw,headers={'Content-Type':'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet','X-CSRF-Token':c.csrf if csrf else ''})
    try:r=c.opener.open(req,timeout=20)
    except urllib.error.HTTPError as error:r=error
    body=r.read().decode('utf-8');assert r.status==expected,(r.status,body);return json.loads(body)

def corrupt(raw):
    out=io.BytesIO();ns={'s':'http://schemas.openxmlformats.org/spreadsheetml/2006/main'}
    with zipfile.ZipFile(io.BytesIO(raw)) as z,zipfile.ZipFile(out,'w',zipfile.ZIP_DEFLATED) as dest:
        for name in z.namelist():
            value=z.read(name)
            if name=='xl/worksheets/sheet1.xml':
                tree=ET.fromstring(value);cell=tree.find('.//s:c[@r="O2"]',ns);cell.clear();cell.attrib.update(r='O2',t='inlineStr');inline=ET.SubElement(cell,'{'+ns['s']+'}is');ET.SubElement(inline,'{'+ns['s']+'}t').text='1A08C000001130020A1111000107D107D1977D';value=ET.tostring(tree,encoding='utf-8')
            dest.writestr(name,value)
    return out.getvalue()

def main():
    exe=Path(sys.argv[1]).resolve();raw=Path(sys.argv[2]).read_bytes();checks=[]
    def check(ok,name):assert ok,name;checks.append(name);print('PASS',name,flush=True)
    with tempfile.TemporaryDirectory(prefix='bc-http-',dir=ROOT/'test-output') as temp:
        root=Path(temp);shutil.copytree(ROOT/'config',root/'config');shutil.copytree(ROOT/'web',root/'web');seed_send_monitor(root)
        for name in ['decoder.json','continuous-rules.json','tc-bindings.json','observed-commands.json']:(root/'config'/name).unlink(missing_ok=True)
        port=free_port();proc=subprocess.Popen([str(exe),'--local','--root',str(root),'--web-port',str(port)],stdout=subprocess.DEVNULL,stderr=subprocess.STDOUT);c=Client(port)
        try:
            def ready():
                try:c.call('/api/auth/me');return True
                except OSError:return False
            wait(ready,'BC HTTP server');password=secrets.token_urlsafe(18);c.call('/api/auth/setup',dict(username='bc_qa',name='BC test',password=password));c.csrf=c.call('/api/auth/login',dict(username='bc_qa',password=password))['csrf']
            path=root/'config/observed-commands.json';before=path.read_bytes();p=upload(c,corrupt(raw));check(not p['canApply'] and any('BC2001' in e and 'Fletcher' in e for e in p['errors']),'bad input row has sheet/code-specific checksum error')
            c.call('/api/send-monitor/commands/apply',dict(token=p['token']),expected=400);check(before==path.read_bytes(),'invalid workbook cannot partially change existing configuration')
            upload(c,raw,csrf=False,expected=403);check(True,'Excel preview requires authenticated CSRF token')
            p=upload(c,raw);check(p['canApply'] and p['bCommands']==1999 and p['broadcastCommands']==21 and p['skipped']==4,'actual workbook preview reports valid/skipped rows and warnings')
            c.call('/api/send-monitor/commands/apply',dict(token=p['token']));catalog=c.call('/api/send-monitor/commands/list?kind=B%E6%8C%87%E4%BB%A4');check(catalog['matching']==1999 and catalog['total']==2020,'B command filter lists imported commands')
            catalog=c.call('/api/send-monitor/commands/list?kind=%E5%B9%BF%E6%92%AD%E4%BB%A4');check(catalog['matching']==21 and all(r['destinations']==['192.168.1.112','192.168.1.113'] for r in catalog['rows']),'broadcast filter includes both destination IPs')
            reply=c.opener.open(c.base+'/api/send-monitor/commands/export.csv');data=reply.read();rows=list(csv.reader(io.StringIO(data.decode('utf-8-sig'))));check(len(rows)==2021 and data.startswith(b'\xef\xbb\xbf') and all(len(r)==6 for r in rows),'CSV export preserves all 2020 full payloads in six-column format')
            before=path.read_bytes();p=upload(c,raw);check(p['canApply'] and p['addedCommands']==0 and p['addedCategories']==0,'repeat workbook preview adds no duplicate codes')
            c.call('/api/send-monitor/commands/apply',dict(token=p['token']));check(path.read_bytes()==before,'repeat workbook import is lossless')
        finally:proc.terminate();proc.wait(timeout=8)
    print('All',len(checks),'BC workbook HTTP checks passed.')
if __name__=='__main__':main()
