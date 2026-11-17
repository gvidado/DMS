"""Validate and cold-start the packaged clean build without an existing database."""
import hashlib,json,secrets,subprocess,sys,tempfile,time,zipfile
from pathlib import Path
from integration_test import ROOT,Client,free_port,wait

def main():
    package=Path(sys.argv[1]).resolve();output=Path(sys.argv[2]).resolve();checks=[]
    def check(ok,label):
        assert ok,label
        checks.append(label);print('PASS',label,flush=True)
    with tempfile.TemporaryDirectory(dir=ROOT/'test-output',prefix='clean-offline-start-') as temporary:
        base=Path(temporary)
        with zipfile.ZipFile(package) as archive:
            assert archive.testzip() is None
            names=archive.namelist();prefix='自动判读系统/'
            check(all(n.startswith(prefix) for n in names) and not any(
                p in n[len(prefix):].split('/') for n in names
                for p in ('data','logs','test-output','examples','.git','__pycache__')),
                'clean package excludes accounts, histories, raw data, logs, test fixtures and imported examples')
            check(not any(n.lower().endswith(('.pcapng','.bin','.docx','.bak','.jsonl','.hst.gz')) for n in names),
                'clean package contains no test captures, injection files, reports or stored history files')
            for name in names:
                target=(base/name).resolve()
                assert target.is_relative_to(base.resolve()),'Unsafe ZIP path'
            archive.extractall(base)
        root=base/'自动判读系统'
        def config(name):return json.loads((root/'config'/name).read_text(encoding='utf-8-sig'))
        settings=config('server.json')
        check(settings['MulticastGroup']=='224.10.2.24' and settings['UdpPort']==30008
              and settings['SourceIPs']==['192.168.1.111','192.168.1.112','192.168.1.113'],
              'clean listener defaults use the requested multicast, UDP port and three source IPs')
        check(settings['BindAddress']=='0.0.0.0' and settings['WebPort']==8080
              and settings['BitOrder']=='msb0' and settings['FramePeriodMs']==160,
              'other listener options retain application defaults')
        check(config('parameters.json')==[] and config('decoder.json')['Packets']==[]
              and config('decoder.json')['Catalog']['Parameters']==[], 'all telemetry tables and SPP definitions are empty')
        check(config('continuous-rules.json')['Rules']==[] and config('tc-bindings.json')['Bindings']==[]
              and config('observed-commands.json')['Commands']==[] and config('sequences.json')['Sequences']==[],
              'rules, TC bindings, command library and sequences are empty')
        check(not (root/'config/history-storage.json').exists() and not (root/'database').exists()
              and not (root/'native/postgresql').exists() and not (root/'tools/初始化历史数据库.ps1').exists(),
              'clean package contains no database configuration, SQL scripts or PostgreSQL client')
        port=free_port();c=Client(port)
        process=subprocess.Popen([str(root/'TelemetryMonitor.exe'),'--local','--root',str(root),'--web-port',str(port)],
            stdout=(base/'startup.log').open('wb'),stderr=subprocess.STDOUT,creationflags=subprocess.CREATE_NO_WINDOW)
        def ready():
            try:c.call('/api/auth/me');return True
            except OSError:return False
        try:
            wait(ready,'clean offline cold start')
            password=secrets.token_urlsafe(20)
            c.call('/api/auth/setup',dict(username='cleanqa',name='全新部署验证',password=password))
            c.csrf=c.call('/api/auth/login',dict(username='cleanqa',password=password))['csrf']
            check(True,'clean offline executable starts and permits creation of its first administrator account')
            check(c.call('/api/catalog')==[] and c.call('/api/continuous-rules')['rows']==[]
                  and c.call('/api/sequence/config')['definitions']==[],
                  'cold-start APIs contain no prior parameters, judgment rules or sequences')
            check(c.call('/api/abnormal-records')['total']==0
                  and c.call('/api/judgment-archives')['history']['total']==0,
                  'cold start has no abnormal records or judgment file archives')
            state=c.call('/api/history/status',expected=404)
            check('error' in state and not (root/'data/history').exists(),
                  'database endpoints are removed and cold start creates no full telemetry archive')
            check(c.call('/api/state')['mode']=='stopped','fresh deployment starts with reception stopped')
        except Exception:
            print((base/'startup.log').read_text(encoding='utf-8',errors='replace')[-2000:]);raise
        finally:process.terminate();process.wait(10)
    report=dict(checks=len(checks),details=checks,package=str(package),sha256=hashlib.sha256(package.read_bytes()).hexdigest())
    (output/'clean-offline-report.json').write_text(json.dumps(report,ensure_ascii=False,indent=2),encoding='utf-8')

if __name__=='__main__':main()
