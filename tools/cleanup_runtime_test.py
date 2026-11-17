"""Exercise record cleanup in a disposable directory with spaces; never target live data."""
import json,shutil,subprocess,sys,tempfile
from pathlib import Path
from integration_test import ROOT

with tempfile.TemporaryDirectory(dir=ROOT/'test-output',prefix='cleanup path ') as temporary:
    root=Path(temporary)
    shutil.copy2(Path(sys.argv[1]),root/'TelemetryMonitor.exe')
    (root/'config').mkdir();(root/'config/server.json').write_bytes(b'{"preserved":true}')
    (root/'data/pending-configuration/config').mkdir(parents=True)
    protected={root/'data/accounts.json':b'account bytes',root/'data/accounts.json.bak':b'backup account bytes',
               root/'config/server.json':b'{"preserved":true}',root/'data/pending-configuration/config/test.json':b'draft bytes'}
    for path,data in protected.items():path.write_bytes(data)
    records={root/'data/archives/sequences/test.jsonl':b'old sequence archive',root/'data/alarm-history.jsonl':b'old alarm',
             root/'logs/example.jsonl':b'old runtime log',root/'data/capture-checks/test.csv':b'old cache'}
    for path,data in records.items():path.parent.mkdir(parents=True,exist_ok=True);path.write_bytes(data)
    result=subprocess.run(['powershell.exe','-NoProfile','-ExecutionPolicy','Bypass','-File',str(ROOT/'tools/清理运行记录.ps1'),'-Root',str(root)],
                          capture_output=True,text=True,encoding='utf-8',errors='replace',creationflags=subprocess.CREATE_NO_WINDOW)
    assert result.returncode==0,result.stdout+result.stderr
    assert all(path.read_bytes()==data for path,data in protected.items())
    assert all(not path.exists() for path in records)
    backup=next((root/'备份').iterdir())
    assert all((backup/path.relative_to(root)).read_bytes()==data for path,data in records.items())
    assert (root/'TelemetryMonitor.exe').read_bytes()==Path(sys.argv[1]).read_bytes()
    print('PASS cleanup removes only runtime records and caches, preserves accounts/configuration/pending edits/executable, and creates an exact backup before deletion')
    (Path(sys.argv[1]).parent/'cleanup-report.json').write_text(json.dumps(dict(passed=True,protected=len(protected),removed=len(records),backedUp=len(records))),encoding='utf-8')
