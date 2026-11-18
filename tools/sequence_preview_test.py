"""Exercise the real start preview in an isolated browser/server, loopback only."""
import json,os,secrets,shutil,socket,subprocess,sys,tempfile
from pathlib import Path
from integration_test import ROOT,Client,free_port,wait
from runtime_benchmark import seed

def main():
    exe=Path(sys.argv[1]).resolve();output=exe.parent
    node=os.environ.get('DMS_BROWSER_NODE','node')
    with tempfile.TemporaryDirectory(prefix='sequence-preview-',dir=ROOT/'test-output') as scratch:
        root=Path(scratch).resolve();assert root.is_relative_to((ROOT/'test-output').resolve())
        seed(root,per_packet=2,packets=1)
        shutil.copytree(ROOT/'web',root/'web')
        def save(name,value):(root/'config'/name).write_text(json.dumps(value,ensure_ascii=False),encoding='utf-8')
        settings=json.loads((root/'config/server.json').read_text('utf-8-sig'));settings['UdpPort']=free_port(socket.SOCK_DGRAM);save('server.json',settings)
        definitions=[]
        for index,mode in enumerate(['judge','execute']):
            rows=[dict(Row=i,Keyword='TEXT',Description='预览步骤'+str(i),Expected='F:10000' if i==53 else '0') for i in range(2,66)]
            rows[0]['Mark']='T0'
            definitions.append(dict(Id=str(index+1)*32,Code=('XLP' if index==0 else 'XLZ')+'0001',Name=('判读' if index==0 else '执行')+'预览演示',Mode=mode,SourceIP='127.0.0.1',Rows=rows))
        save('sequences.json',dict(SchemaVersion=1,Revision='preview-fixture',DefaultTimeoutSeconds=30,Sequences=definitions))
        port=free_port();client=Client(port);password=secrets.token_urlsafe(20)
        log=(root/'server.log').open('wb');process=subprocess.Popen([str(exe),'--local','--root',str(root),'--web-port',str(port)],stdout=log,stderr=subprocess.STDOUT,creationflags=subprocess.CREATE_NO_WINDOW)
        def ready():
            try:client.call('/api/auth/me');return True
            except OSError:return False
        try:
            wait(ready,'isolated preview server')
            client.call('/api/auth/setup',dict(username='previewqa',name='预览检查',password=password))
            auth=root/'browser-auth.json';auth.write_text(json.dumps(dict(base=client.base,password=password,out=str(output))),encoding='utf-8')
            result=subprocess.run([node,str(ROOT/'tools/sequence_preview_browser.mjs'),str(auth)],capture_output=True,text=True,encoding='utf-8',timeout=90,creationflags=subprocess.CREATE_NO_WINDOW)
            print(result.stdout,flush=True)
            if result.returncode:raise RuntimeError(result.stderr)
        finally:
            process.terminate();process.wait(10);log.close()

if __name__=='__main__':main()
