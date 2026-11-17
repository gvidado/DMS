"""Prove legacy database settings cannot activate connections or mutate archives."""
import json,secrets,select,shutil,socket,subprocess,sys,tempfile,urllib.error
from pathlib import Path
from integration_test import ROOT,Client,free_port,wait
from runtime_benchmark import seed

def main():
    exe=Path(sys.argv[1]).resolve();checks=[]
    def check(ok,label):assert ok,label;checks.append(label);print('PASS',label,flush=True)
    with tempfile.TemporaryDirectory(dir=ROOT/'test-output',prefix='no-database-') as temporary:
        root=Path(temporary);seed(root,per_packet=2,packets=1);shutil.copytree(ROOT/'web',root/'web');(root/'data').mkdir()
        archives=[root/'data/history',root/'old-external-history']
        for directory in archives:directory.mkdir();(directory/'preserve-old-data.bin').write_bytes(bytes(range(256))*4)
        def stored():return {str(file.relative_to(root)):file.read_bytes() for directory in archives for file in directory.rglob('*') if file.is_file()}
        original=stored();config_path=root/'config/history-storage.json';config_path.write_text('{invalid legacy configuration',encoding='utf-8')
        probe=socket.socket();probe.bind(('127.0.0.1',0));probe.listen();port=free_port();c=Client(port);password=secrets.token_urlsafe(18);process=None;log=(root/'server.log').open('wb')
        def launch():return subprocess.Popen([str(exe),'--local','--root',str(root),'--web-port',str(port)],stdout=log,stderr=subprocess.STDOUT,creationflags=subprocess.CREATE_NO_WINDOW)
        def ready():
            try:c.call('/api/auth/me');return True
            except OSError:return False
        def login():c.csrf=c.call('/api/auth/login',dict(username='nodbqa',password=password))['csrf']
        def stop():process.terminate();process.wait(10)
        try:
            process=launch();wait(ready,'server without database');c.call('/api/auth/setup',dict(username='nodbqa',name='无数据库验证',password=password));login()
            check(c.call('/api/catalog') and c.call('/api/config')['UdpPort']>0,'server starts and loads normal configuration despite malformed legacy database settings')
            for endpoint in ['status','config','query','options','experiments','import/status','export.csv','export.xlsx','dataset/status','dataset/export']:
                check('error' in c.call('/api/history/'+endpoint,expected=404),'removed API returns 404: '+endpoint)
            for asset in ['history.js','history-filters.js','history.css']:
                try:c.opener.open(c.base+'/'+asset);raise AssertionError('Retired asset still served: '+asset)
                except urllib.error.HTTPError as error:assert error.code==404
            check(True,'retired database scripts and styles are not served')
            check(c.call('/api/continuous-rules')['rows'] and c.call('/api/judgment-archives')['history']['total']==0 and c.call('/api/sequence/config')['definitions']==[],'continuous judgment, independent judgment archives and sequence APIs remain available')
            if '--browser' in sys.argv:
                runtime=Path.home()/'.cache/codex-runtimes/codex-primary-runtime/dependencies/node'
                result=subprocess.run([str(runtime/'bin/node.exe'),str(ROOT/'tools/no_database_browser.mjs'),str(runtime)],input=json.dumps(dict(base=c.base,password=password)),text=True,encoding='utf-8',capture_output=True,timeout=70,creationflags=subprocess.CREATE_NO_WINDOW)
                assert result.returncode==0,result.stdout+result.stderr
                check('PASS' in result.stdout,'real browser retains all six sections and sends no database requests')
            stop();config_path.write_text(json.dumps(dict(SchemaVersion=1,DatabaseEnabled=True,Host='127.0.0.1',Port=probe.getsockname()[1],Database='old_history',Username='unused',ArchiveDirectory=str(archives[1]),EncryptedPassword='invalid-old-ciphertext')),encoding='utf-8')
            enabled_bytes=config_path.read_bytes();c=Client(port);process=launch();wait(ready,'old enabled settings ignored');login()
            check(c.call('/api/state')['mode']=='stopped','old enabled database settings and unreadable old credentials do not prevent restart')
            for _ in range(6):
                assert not select.select([probe],[],[],0.2)[0],'Unexpected database connection'
                c.call('/api/state')
            check(True,'no connection is attempted even when old configuration enables PostgreSQL')
            check(stored()==original and config_path.read_bytes()==enabled_bytes,'all previous database archives and legacy connection settings remain byte-for-byte unchanged')
            check(not any('history' in path.name and path.suffix=='.tmp' for path in root.rglob('*')),'no retired database archive/import jobs are resumed')
            (exe.parent/'no-database-report.json').write_text(json.dumps(dict(checks=len(checks),details=checks),ensure_ascii=False,indent=2),encoding='utf-8')
        except Exception:
            log.flush();print((root/'server.log').read_text('utf-8',errors='replace')[-1800:]);raise
        finally:
            if process and process.poll() is None:process.terminate();process.wait(10)
            probe.close();log.close()
    print('All',len(checks),'database removal checks passed.')

if __name__=='__main__':main()
