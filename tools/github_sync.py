"""Prepare/publish a clean DMS source snapshot and already verified release assets.

Uses Python's standard library and Git Credential Manager. Never stores tokens.
Run prepare first to inspect the concrete changes; publish performs the authorized sync.
"""
import argparse,hashlib,json,os,re,shutil,subprocess,sys,urllib.error,urllib.parse,urllib.request,zipfile
from pathlib import Path

OWNER_REPO='gvidado/DMS'
PROJECT=Path(__file__).resolve().parents[1]
WORKSPACE=PROJECT.parent
DEFAULT_REPO=PROJECT if (PROJECT/'.git').is_dir() else WORKSPACE/'GitHub维护'/'DMS'

def digest(data):return hashlib.sha256(data).hexdigest()
def read_json(path):return json.loads(Path(path).read_text('utf-8-sig'))
def write_json(path,value):
    path=Path(path);path.parent.mkdir(parents=True,exist_ok=True)
    path.write_text(json.dumps(value,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')

def git(repo,*args,check=True):
    env=dict(os.environ,GIT_TERMINAL_PROMPT='0',GCM_INTERACTIVE='Never')
    p=subprocess.run(['git','-C',str(repo),*args],capture_output=True,text=True,encoding='utf-8',errors='replace',env=env)
    if check and p.returncode:raise RuntimeError('Git operation failed: '+args[0]+'\n'+p.stderr[-1500:])
    return p

def version(project):
    m=re.search(r'AssemblyInformationalVersion\("([0-9]+\.[0-9]+\.[0-9]+)"\)',(project/'src/AssemblyInfo.cs').read_text('utf-8-sig'))
    if not m:raise RuntimeError('Cannot read system version')
    return m.group(1)

def credential():
    token=os.environ.get('GH_TOKEN') or os.environ.get('GITHUB_TOKEN')
    if token:return token
    env=dict(os.environ,GIT_TERMINAL_PROMPT='0',GCM_INTERACTIVE='Never')
    p=subprocess.run(['git','credential','fill'],input='protocol=https\nhost=github.com\npath=gvidado/DMS.git\n\n',capture_output=True,text=True,env=env,timeout=30)
    if p.returncode:return None
    fields=dict(line.split('=',1) for line in p.stdout.splitlines() if '=' in line)
    return fields.get('password')

def api(path,token=None,method='GET',body=None,content_type='application/json'):
    url=path if path.startswith('https://') else 'https://api.github.com'+path
    if not url.startswith(('https://api.github.com/','https://uploads.github.com/')):raise ValueError('Unexpected API host')
    data=json.dumps(body,ensure_ascii=False).encode('utf-8') if isinstance(body,dict) else body
    headers={'Accept':'application/vnd.github+json','X-GitHub-Api-Version':'2022-11-28','User-Agent':'DMS-release-maintenance'}
    if token:headers['Authorization']='Bearer '+token
    if data is not None:headers['Content-Type']=content_type
    try:
        with urllib.request.urlopen(urllib.request.Request(url,data=data,headers=headers,method=method),timeout=120) as response:
            payload=response.read();return response.status,json.loads(payload) if payload else None
    except urllib.error.HTTPError as error:
        try:detail=json.loads(error.read())
        except ValueError:detail={'message':'GitHub API request failed'}
        return error.code,detail

def expect(status,result,wanted):
    if status not in wanted:raise RuntimeError('GitHub API '+str(status)+': '+str(result.get('message','request failed')))
    return result

def check_repo(repo):
    remote=git(repo,'remote','get-url','origin').stdout.strip()
    if remote not in ('https://github.com/'+OWNER_REPO+'.git','https://github.com/'+OWNER_REPO):raise RuntimeError('Unexpected origin; refusing to publish')

def checked_zip(path,prefix,exe,web):
    with zipfile.ZipFile(path) as archive:
        if archive.testzip():raise RuntimeError('Corrupt package '+path.name)
        names=archive.namelist()
        for name in names:
            if not name.startswith(prefix):raise RuntimeError('Unexpected package root')
            rel=name[len(prefix):];parts=Path(rel).parts
            if any(p in ('data','logs','test-output','.git','备份','提供材料','__pycache__') for p in parts):raise RuntimeError('Excluded deployment content in package: '+rel)
            if rel.lower().endswith(('.bak','.pcap','.pcapng','.bin','.jsonl','.hst.gz','.key','.pem')):raise RuntimeError('Excluded attachment in package: '+rel)
            if 'database' in parts and rel not in ('database/initialize-history.sql','database/history-schema-v1.sql'):
                raise RuntimeError('Unexpected database content: '+rel)
            if 'postgresql' in parts and not (rel.startswith('native/postgresql/') and len(parts)==3 and
                    (parts[2].endswith('.dll') or parts[2].startswith('LICENSE') or parts[2] in ('commandlinetools_3rd_party_licenses.txt','server_license.txt','SOURCE.txt'))):
                raise RuntimeError('Unexpected PostgreSQL content: '+rel)
        if digest(archive.read(prefix+'TelemetryMonitor.exe'))!=digest(exe.read_bytes()):raise RuntimeError('Executable differs from current build')
        for file in web.iterdir():
            if file.is_file() and digest(archive.read(prefix+'web/'+file.name))!=digest(file.read_bytes()):raise RuntimeError('Web resource differs: '+file.name)
        if prefix:
            defaults={Path(n).name:json.loads(archive.read(n).decode('utf-8-sig')) for n in names if n.startswith(prefix+'config/') and n.endswith('.json')}
            s=defaults['server.json']
            if s['MulticastGroup']!='224.10.2.24' or s['UdpPort']!=30008 or s['SourceIPs']!=['192.168.1.111','192.168.1.112','192.168.1.113']:raise RuntimeError('Unexpected clean listener defaults')
            if defaults['parameters.json'] or defaults['decoder.json']['Catalog']['Parameters'] or defaults['decoder.json']['Packets']:raise RuntimeError('Imported telemetry in clean package')
            for n,k in [('continuous-rules.json','Rules'),('tc-bindings.json','Bindings'),('sequences.json','Sequences'),('observed-commands.json','Commands')]:
                if defaults[n][k]:raise RuntimeError('Imported library in clean package: '+n)
            history=defaults.get('history-storage.json')
            if history and (history.get('DatabaseEnabled') or history.get('ArchiveEnabled') or history.get('EncryptedPassword') or history.get('ArchiveDirectory')):
                raise RuntimeError('Existing history settings in clean package')
            return defaults
        if any(n.startswith('config/') for n in names):raise RuntimeError('Upgrade must preserve现场config')
    return None

def prepare(project,repo,include_dsh=False,release_executable=None):
    check_repo(repo);v=version(project);release=repo.parent/'releases'/('v'+v);release.mkdir(parents=True,exist_ok=True)
    offline=project.parent/'离线包'/('自动判读系统_v'+v+'_干净离线包.zip')
    upgrade=project.parent/'升级包'/('自动判读系统_v'+v+'_程序更新包.zip')
    for file in (offline,upgrade):
        if not file.is_file():raise RuntimeError('Build release package first: '+str(file))
    executable=release_executable or project/'TelemetryMonitor.exe'
    defaults=checked_zip(offline,'自动判读系统/',executable,project/'web')
    checked_zip(upgrade,'',executable,project/'web')
    managed={}
    def put(rel,data):
        path=repo/rel;path.parent.mkdir(parents=True,exist_ok=True);path.write_bytes(data);managed[rel]=digest(data)
    def copy(rel,src):put(rel,Path(src).read_bytes())
    for folder,extensions in [('src',{'.cs'}),('web',{'.html','.css','.js','.svg','.png','.woff','.woff2'})]:
        for file in sorted((project/folder).rglob('*')):
            if file.is_file() and file.suffix.lower() in extensions:copy(file.relative_to(project).as_posix(),file)
    for name in ['database/initialize-history.sql','database/history-schema-v1.sql']:copy(name,project/name)
    for name in ['build.ps1','TelemetryMonitor.exe.config','本机预览.cmd','启动服务器.cmd','配置防火墙.ps1']:copy(name,project/name)
    if (project/'AGENTS.md').exists():copy('AGENTS.md',project/'AGENTS.md')
    skipped=[]
    for file in sorted((project/'tools').iterdir()):
        if not file.is_file() or file.suffix not in ('.py','.ps1','.cs','.mjs','.md'):continue
        if file.name.startswith(('history_','History','generate_manual','beautify_manual')):continue
        content=file.read_text('utf-8-sig')
        if file.suffix in ('.py','.mjs','.cs') and re.search(r'(?i)[cdef]:[\\/]',content):
            skipped.append(file.name);continue
        copy('tools/'+file.name,file)
    # Optional DSH source is shared with its own development task; no installed state.
    plugin=project/'dsh-plugin'
    if include_dsh and plugin.is_dir():
        for file in sorted(plugin.rglob('*')):
            rel=file.relative_to(plugin)
            if file.is_file() and not any(p in ('dist','node_modules','.git') for p in rel.parts) and file.suffix in ('.js','.mjs','.json','.md','.yml','.ps1'):
                copy('dsh-plugin/'+rel.as_posix(),file)
    for name,value in defaults.items():put('config.example/'+name,(json.dumps(value,ensure_ascii=False,indent=2)+'\n').encode('utf-8'))
    notes_path=project/'说明'/('V'+v+'更新说明.md')
    for file in sorted(set((project/'说明').glob('V0.10.*说明.md')) | {notes_path}):
        copy('说明/'+file.name,file)
    # Avoid historical manuals, user materials and the local migration document.
    template=(project/'tools/github_README.template.md').read_text('utf-8')
    guides=[]
    for file in (project/'说明').glob('数据判读系统_V*_使用说明书.docx'):
        m=re.fullmatch(r'数据判读系统_V([0-9]+\.[0-9]+\.[0-9]+)_使用说明书\.docx',file.name)
        if m and tuple(map(int,m[1].split('.')))<=tuple(map(int,v.split('.'))):guides.append((tuple(map(int,m[1].split('.'))),m[1],file))
    if not guides:raise RuntimeError('A versioned user manual is required before publishing')
    _,manual_version,manual=max(guides)
    put('README.md',template.replace('{{VERSION}}',v).replace('{{MANUAL_VERSION}}',manual_version).encode('utf-8'))
    copy('docs/'+manual.name,manual)
    shots=project.parent/'文档编制暂存'/('使用说明书_V'+manual_version)
    for filename,relative in [('配图_02_实时参数.png','parameters.png'),('配图_07_序列运行.png','sequence.png')]:
        image=shots/filename
        if image.exists():copy('docs/screenshots/'+relative,image)
        elif not (repo/'docs/screenshots'/relative).exists():raise RuntimeError('Demo screenshot is missing')
    assets=[]
    for original,name in [(offline,'DMS-v'+v+'-windows-offline.zip'),(upgrade,'DMS-v'+v+'-windows-upgrade.zip'),(manual,'DMS-v'+manual_version+'-user-manual.docx')]:
        target=release/name;shutil.copyfile(original,target);assets.append({'name':name,'size':target.stat().st_size,'sha256':digest(target.read_bytes())})
    sums=''.join(a['sha256']+'  '+a['name']+'\n' for a in assets);(release/'SHA256SUMS.txt').write_text(sums,encoding='utf-8')
    assets.append({'name':'SHA256SUMS.txt','size':(release/'SHA256SUMS.txt').stat().st_size,'sha256':digest((release/'SHA256SUMS.txt').read_bytes())})
    notes=(project/'说明'/('V'+v+'更新说明.md')).read_text('utf-8-sig')
    notes+='\n\n## 下载与使用\n\n- windows-offline.zip：新部署，空配置，不含现场账号或数据。\n- windows-upgrade.zip：覆盖更新，保留config、data、logs。\n- user-manual.docx：完整使用说明和语法示例。\n- SHA256SUMS.txt：下载校验。\n\nPostgreSQL 用于可选的历史数据库功能。安装Npcap后可使用相应抓包监视能力。升级前停止程序，覆盖后重启并Ctrl+F5刷新网页。\n'
    if manual_version!=v:notes+='\n随附完整使用说明适用V'+manual_version+'，后续变化以本次版本说明为准。\n'
    (release/'release-notes.md').write_text(notes,encoding='utf-8')
    put('release/current.json',(json.dumps({'version':v,'tag':'v'+v,'manualVersion':manual_version,'assets':assets},ensure_ascii=False,indent=2)+'\n').encode('utf-8'))
    # Reject common credential formats without printing possible credential values.
    patterns=[r'github_pat_[A-Za-z0-9_]{20,}',r'gh[pousr]_[A-Za-z0-9]{25,}',r'sk-[A-Za-z0-9]{24,}',r'-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----']
    for rel in managed:
        if Path(rel).suffix in ('.docx','.png'):continue
        text=(repo/rel).read_text('utf-8-sig')
        if any(re.search(p,text) for p in patterns):raise RuntimeError('Possible credential in '+rel+'; review before publication')
    old=repo/'release/source-manifest.json'
    if old.exists():
        previous=read_json(old)
        for rel in previous.get('files',{}):
            if rel not in managed:
                path=(repo/rel).resolve()
                if not path.is_relative_to(repo.resolve()) or '.git' in path.parts:raise RuntimeError('Invalid stale managed path')
                if path.is_file():path.unlink()
    write_json(old,{'version':v,'files':managed})
    report={'version':v,'tag':'v'+v,'source_files':len(managed),'assets':assets,'skipped_nonportable_tools':skipped,'prepared':True}
    write_json(release/'preparation-report.json',report)
    print(json.dumps(report,ensure_ascii=False,indent=2))

def publish(project,repo):
    check_repo(repo);v=version(project);release=repo.parent/'releases'/('v'+v);plan=read_json(release/'preparation-report.json')
    if plan['version']!=v or not plan['prepared']:raise RuntimeError('Prepare this exact version first')
    manifest=read_json(repo/'release/source-manifest.json')
    for rel,wanted in manifest['files'].items():
        if digest((repo/rel).read_bytes())!=wanted:raise RuntimeError('Prepared source changed: '+rel)
    for asset in plan['assets']:
        if digest((release/asset['name']).read_bytes())!=asset['sha256']:raise RuntimeError('Prepared asset changed')
    token=credential()
    if not token:raise RuntimeError('GitHub login required; run git credential-manager github login --browser --username gvidado')
    status,user=api('/user',token);expect(status,user,{200})
    status,remote_repo=api('/repos/'+OWNER_REPO,token);expect(status,remote_repo,{200})
    if not remote_repo.get('permissions',{}).get('push'):raise RuntimeError('This GitHub account cannot push to the repository')
    git(repo,'config','--local','user.name',user.get('name') or user['login'])
    git(repo,'config','--local','user.email',str(user['id'])+'+'+user['login']+'@users.noreply.github.com')
    git(repo,'fetch','origin')
    git(repo,'merge-base','--is-ancestor','origin/main','HEAD')
    files=list(manifest['files'])+['release/source-manifest.json','CHANGELOG.md','RELEASING.md','.gitignore','.gitattributes','.github/workflows/build.yml','AGENTS.md']
    files=[f for f in files if (repo/f).is_file()]
    git(repo,'add','--',*files)
    # Stage previously managed deletions without adding unrelated working files.
    git(repo,'add','-u')
    changes=git(repo,'diff','--cached','--quiet',check=False)
    if changes.returncode==1:git(repo,'commit','-m','Release DMS v'+v+' with source, documentation and release workflow')
    elif changes.returncode!=0:raise RuntimeError('Cannot inspect staged changes')
    sha=git(repo,'rev-parse','HEAD').stdout.strip();tag='v'+v
    old=git(repo,'rev-parse','--verify','refs/tags/'+tag,check=False)
    if old.returncode:git(repo,'tag','-a',tag,'-m','DMS V'+v)
    elif git(repo,'rev-list','-n','1',tag).stdout.strip()!=sha:raise RuntimeError('Existing tag points elsewhere; do not overwrite it')
    git(repo,'push','--atomic','origin','HEAD:refs/heads/main','refs/tags/'+tag)
    endpoint='/repos/'+OWNER_REPO+'/releases'
    status,entry=api(endpoint+'/tags/'+tag,token)
    notes=(release/'release-notes.md').read_text('utf-8')
    if status==404:
        status,entry=api(endpoint,token,'POST',{'tag_name':tag,'target_commitish':sha,'name':'DMS V'+v,'body':notes,'draft':True,'prerelease':False});expect(status,entry,{201})
    else:expect(status,entry,{200})
    for asset in plan['assets']:
        existing=next((a for a in entry.get('assets',[]) if a['name']==asset['name']),None)
        if existing:
            # Published assets are never silently replaced.
            if existing['size']!=asset['size']:raise RuntimeError('Existing release asset differs; use a new version')
            remote_digest=existing.get('digest')
            if remote_digest and remote_digest!='sha256:'+asset['sha256']:raise RuntimeError('Existing release asset checksum differs')
            continue
        url=entry['upload_url'].split('{',1)[0]+'?name='+urllib.parse.quote(asset['name'])
        status,result=api(url,token,'POST',(release/asset['name']).read_bytes(),'application/octet-stream');expect(status,result,{201})
    status,entry=api(endpoint+'/'+str(entry['id']),token,'PATCH',{'name':'DMS V'+v,'body':notes,'draft':False,'prerelease':False,'make_latest':'true'});expect(status,entry,{200})
    status,entry=api(endpoint+'/tags/'+tag,token);expect(status,entry,{200})
    actual={a['name']:a['size'] for a in entry['assets']}
    if entry['draft'] or not all(actual.get(a['name'])==a['size'] for a in plan['assets']):raise RuntimeError('Published asset verification failed')
    status,about=api('/repos/'+OWNER_REPO,token,'PATCH',{'description':'DMS 数据判读系统：离线UDP/SPP遥测解码、常态化判读、序列任务与指令控制'})
    result={'version':v,'commit':sha,'repository':'https://github.com/'+OWNER_REPO,'release':entry['html_url'],'assets':actual,'description_updated':status==200}
    write_json(release/'published.json',result);print(json.dumps(result,ensure_ascii=False,indent=2))

def main():
    p=argparse.ArgumentParser(description=__doc__);p.add_argument('action',choices=['prepare','publish']);p.add_argument('--project-root',type=Path,default=PROJECT);p.add_argument('--repo-root',type=Path,default=DEFAULT_REPO);p.add_argument('--release-executable',type=Path,help='Use the tested build while an older server remains running');p.add_argument('--include-dsh',action='store_true',help='Include a separately verified DSH plugin source snapshot');args=p.parse_args()
    if args.action=='prepare':prepare(args.project_root.resolve(),args.repo_root.resolve(),args.include_dsh,args.release_executable.resolve() if args.release_executable else None)
    else:publish(args.project_root.resolve(),args.repo_root.resolve())

if __name__=='__main__':main()
