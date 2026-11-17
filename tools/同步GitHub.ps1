param(
    [ValidateSet('Prepare','Publish')][string]$Action='Publish',
    [string]$Python='python',
    [string]$RepositoryRoot=''
)
$ErrorActionPreference='Stop'
$scriptPath=Join-Path $PSScriptRoot 'github_sync.py'
$arguments=@()
if($RepositoryRoot){$arguments+=@('--repo-root',$RepositoryRoot)}
& $Python $scriptPath prepare @arguments
if($LASTEXITCODE -ne 0){throw 'GitHub同步准备失败，尚未发布。'}
if($Action -eq 'Publish'){
    & $Python $scriptPath publish @arguments
    if($LASTEXITCODE -ne 0){throw 'GitHub同步未全部完成，请核对错误和远程状态后重试。'}
}
