"""Exercise the six-column rule CSV download on an isolated local server."""
import csv, io, json, secrets, shutil, subprocess, sys, tempfile
from pathlib import Path
from integration_test import ROOT, Client, free_port, wait, seed_send_monitor
from continuous_rules_test import HEADERS

def main():
    executable=Path(sys.argv[1]).resolve() if len(sys.argv)>1 else ROOT/'TelemetryMonitor.exe'
    with tempfile.TemporaryDirectory(prefix='rules-csv-') as temporary:
        directory=Path(temporary);shutil.copytree(ROOT/'config',directory/'config');shutil.copytree(ROOT/'web',directory/'web');seed_send_monitor(directory)
        (directory/'config/decoder.json').unlink(missing_ok=True)
        rules=[]
        for index in range(56):
            rules.append(dict(Id='csv-'+str(index),Code='A'+str(index+1),Name='参数,"名称"\n第'+str(index+1)+'项',Level=['一般','重要','关键'][index%3],Enabled=index%2==0,
                              Script='' if index==55 else '// 中文,"注释"\r\n\tRANGE=[-1 1];',ParameterId='csv/'+str(index),SourceIP='127.0.0.1',Bindings={}))
        config=dict(SchemaVersion=1,Revision='csv-fixture',Rules=rules);path=directory/'config/continuous-rules.json';path.write_text(json.dumps(config,ensure_ascii=False),encoding='utf-8');before=path.read_bytes()
        port=free_port();process=subprocess.Popen([str(executable),'--local','--root',str(directory),'--web-port',str(port)],stdout=subprocess.DEVNULL,stderr=subprocess.STDOUT)
        try:
            client=Client(port)
            def ready():
                try:return client.call('/api/auth/me')['setupRequired']
                except OSError:return False
            wait(ready,'CSV server');client.call('/api/continuous-rules/export.csv',expected=401)
            password=secrets.token_urlsafe(18);client.call('/api/auth/setup',dict(username='csvqa',name='CSV QA',password=password));client.csrf=client.call('/api/auth/login',dict(username='csvqa',password=password))['csrf']
            with client.opener.open(client.base+'/api/continuous-rules/export.csv?search=nomatch',timeout=10) as response:
                assert response.headers.get_content_type()=='text/csv';assert 'continuous-rules.csv' in response.headers['Content-Disposition'];raw=response.read()
            assert raw.startswith(b'\xef\xbb\xbf'),'UTF-8 BOM missing'
            exported=list(csv.reader(io.StringIO(raw.decode('utf-8-sig'),newline='')))
            assert exported[0]==HEADERS,exported[0]
            assert len(exported)==57,'all rows, including other pages and disabled defaults, must be exported'
            for index,(actual,rule) in enumerate(zip(exported[1:],rules),1):
                assert actual==[str(index),rule['Code'],rule['Name'],rule['Level'],'是' if rule['Enabled'] else '否',rule['Script']],(index,actual)
            assert exported[-1][-1]=='','empty initial rule must stay empty'
            assert path.read_bytes()==before,'export must not change configuration'
            original=client.call('/api/continuous-rules/export');assert original['Revision']=='csv-fixture' and len(original['Rules'])==56,'JSON export changed'
            assert 'id="rule-export-csv"' in (ROOT/'web/continuous.js').read_text(encoding='utf-8')
            print('PASS authenticated six-column CSV, Excel UTF-8 BOM, all 56 rows, flags and levels, empty defaults, exact multiline/quote/comma round-trip, read-only export and existing JSON download')
        finally:
            if process.poll() is None:process.terminate();process.wait(timeout=6)

if __name__=='__main__':main()
