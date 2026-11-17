"""Compile-independent runner for the isolated no-socket long-run receive regression."""
import json,subprocess,sys,tempfile
from pathlib import Path
from integration_test import ROOT
from runtime_benchmark import seed

def main():
 host=Path(sys.argv[1]).resolve();output=Path(sys.argv[2]).resolve()
 with tempfile.TemporaryDirectory(dir=ROOT/'test-output',prefix='longrun-receive-') as scratch:
  root=Path(scratch);seed(root,per_packet=2,packets=1)
  for name in ['decoder.json','continuous-rules.json']:
   path=root/'config'/name;value=json.loads(path.read_text(encoding='utf-8'))
   if name=='decoder.json':
    for packet in value['Packets']:packet['MonitorRaw']=False
   else:
    for rule in value['Rules']:rule['Enabled']=False
   path.write_text(json.dumps(value),encoding='utf-8')
  result=subprocess.run([str(host),str(root)],capture_output=True,text=True,encoding='utf-8',errors='replace',timeout=180,creationflags=subprocess.CREATE_NO_WINDOW)
  print(result.stdout,flush=True)
  if result.returncode:raise AssertionError(result.stderr[-5000:])
  report=json.loads(result.stdout.strip().splitlines()[-1]);output.parent.mkdir(parents=True,exist_ok=True);output.write_text(json.dumps(report,ensure_ascii=False,indent=2),encoding='utf-8')

if __name__=='__main__':main()
