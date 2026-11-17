param([string]$ReleaseExecutable = '')
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
& (Join-Path $PSScriptRoot 'normalize_launchers.ps1')
$releaseRoot = Split-Path -Parent $projectRoot
$offlineDir = Join-Path $releaseRoot '离线包'
$upgradeDir = Join-Path $releaseRoot '升级包'
New-Item -ItemType Directory -Path $offlineDir, $upgradeDir -Force | Out-Null
if (!$ReleaseExecutable) { $ReleaseExecutable = Join-Path $projectRoot 'TelemetryMonitor.exe' }
$ReleaseExecutable = (Get-Item -LiteralPath $ReleaseExecutable).FullName
if ([Diagnostics.FileVersionInfo]::GetVersionInfo($ReleaseExecutable).FileVersion -ne '0.10.26.0') { throw 'Expected version 0.10.26.0' }
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$files = [Collections.Generic.List[object]]::new()
function Select-File([string]$path, [string]$relative) {
    if (Test-Path -LiteralPath $path -PathType Leaf) { $files.Add([pscustomobject]@{ File = (Get-Item -LiteralPath $path); Relative = $relative.Replace('\','/') }) }
}
foreach ($folder in @('web','native')) {
    $base = Join-Path $projectRoot $folder
    if (!(Test-Path -LiteralPath $base)) { continue }
    Get-ChildItem -LiteralPath $base -File -Recurse | Where-Object { $_.Extension -notin @('.pyc','.bak','.tmp') -and $_.FullName -notlike '*\__pycache__\*' -and $_.FullName -notlike '*\历史根目录文件\*' -and $_.FullName -notlike '*\native\postgresql\*' } | ForEach-Object { Select-File $_.FullName $_.FullName.Substring($projectRoot.Length+1) }
}
foreach ($name in @('TelemetryMonitor.exe.config','启动服务器.cmd','本机预览.cmd','配置防火墙.ps1')) { Select-File (Join-Path $projectRoot $name) $name }
foreach ($name in @('指令面板与自由布局说明.md','参数画面配置说明.md','V0.10.26更新说明.md','V0.10.25更新说明.md','V0.10.24更新说明.md','V0.10.23更新说明.md','V0.10.22更新说明.md','V0.10.21更新说明.md','V0.10.20更新说明.md','V0.10.19更新说明.md','V0.10.18更新说明.md','V0.10.17更新说明.md','V0.10.16接收计数重同步修复说明.md','V0.10.15XML指令导入更新说明.md','V0.10.13更新说明.md','V0.10.12更新说明.md','V0.10.11更新说明.md','V0.10.10更新说明.md')) { Select-File (Join-Path $projectRoot ('说明/'+$name)) ('说明/'+$name) }
Select-File (Join-Path $projectRoot 'tools/清理运行记录.ps1') 'tools/清理运行记录.ps1'
Select-File $ReleaseExecutable 'TelemetryMonitor.exe'
# Generate fresh defaults from application types; never load existing user settings.
$candidateDir = Split-Path -Parent $ReleaseExecutable
$helper = Join-Path $candidateDir 'CleanOfflineDefaults.exe'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64/v4.0.30319/csc.exe'
& $compiler /nologo /target:exe /platform:anycpu /codepage:65001 /r:System.dll /r:System.Core.dll /r:System.Web.Extensions.dll "/r:$ReleaseExecutable" "/out:$helper" (Join-Path $PSScriptRoot 'CleanOfflineDefaults.cs')
if ($LASTEXITCODE -ne 0) { throw 'Clean defaults helper build failed' }
$cleanRoot = Join-Path $candidateDir ('clean-offline-'+[Guid]::NewGuid().ToString('N'))
& $helper $cleanRoot
if ($LASTEXITCODE -ne 0) { throw 'Clean defaults generation failed' }
$cleanFiles = [Collections.Generic.List[object]]::new()
foreach ($item in $files) { $cleanFiles.Add($item) }
Get-ChildItem -LiteralPath (Join-Path $cleanRoot 'config') -File |
    ForEach-Object { $cleanFiles.Add([pscustomobject]@{ File = $_; Relative = 'config/'+$_.Name }) }

function Add-ReleaseFile($archive, $item, [string]$name) {
    $source = [IO.File]::Open($item.File.FullName,[IO.FileMode]::Open,[IO.FileAccess]::Read,([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
    try { $entry=$archive.CreateEntry($name,[IO.Compression.CompressionLevel]::Optimal);$entry.LastWriteTime=$item.File.LastWriteTime;$target=$entry.Open();try{$source.CopyTo($target)}finally{$target.Dispose()} }finally{$source.Dispose()}
}
$instructions = @'
数据判读系统 V0.10.26

本版修正指令接口类型长度：功能ID为0xAA（十进制170）时2字节，其余功能ID4字节，均为小端。XML/手动入库、发送预览、实际发送、接收匹配和TC采用同一规则。原有多选、对齐、等距分布与序列功能继续保留。详见说明/V0.10.26更新说明.md。

继续保留V0.10.22的数据库移除状态：历史查询、参数历史查询、通讯历史、数据底座全量导出、历史存储配置，以及PostgreSQL连接、入库和全量原包/样本归档后台均已移除。接收和配置切换不再触发数据库操作。软件无需安装或运行数据库。

监听、遥测解码、实时显示、SPP原包最近200条、常态化判读、序列执行/判读、指令库和指令面板继续使用。原有判读存档、序列运行记录、异常确认和CSV/Excel导出继续保留；判读所需的短期工程值历史仍在内存中运行。

功能ID为0xAA时数据区从第25字节开始，其他ID从第27字节开始。旧AA四字节接口自动转换为两字节，保留数据并重算外层长度及校验；旧接口值超过65535时保留源码并禁止发送，需人工更正。编号、名称、目的IP、TC及序列关联保留；普通UDP原样发送。详见说明/V0.10.26更新说明.md。

升级包：停止旧程序，覆盖本包全部文件，保留原config、data、logs及独立历史目录，重新启动并按Ctrl+F5刷新网页。本包不自动清空配置或历史文件；如需清空运行记录，可停止程序后执行tools/清理运行记录.ps1，保留账号及配置并自动备份。不连接、卸载、停止或删除电脑上已有的PostgreSQL及其数据。旧历史存储配置即使仍存在，也不会读取或启用。已有数据库文件仍占磁盘，数据库清理须另行进行。

干净离线包：请解压到新的空目录部署。监听组播224.10.2.24、UDP端口30008、来源192.168.1.111～192.168.1.113，其余使用默认配置。参数表、SPP包、规则、指令库、TC绑定和序列均为空。包内没有账号、连接密码、历史数据、数据库脚本或PostgreSQL客户端组件。首次使用在本机网页创建管理员账号。

'@
function Write-Package([string]$path, $items, [string]$prefix) {
    $stream=[IO.File]::Open($path,[IO.FileMode]::Create)
    try { $zip=[IO.Compression.ZipArchive]::new($stream,[IO.Compression.ZipArchiveMode]::Create,$true)
        try { foreach($item in $items){Add-ReleaseFile $zip $item ($prefix+$item.Relative)}
            $entry=$zip.CreateEntry($prefix+'更新说明.txt');$writer=[IO.StreamWriter]::new($entry.Open(),[Text.UTF8Encoding]::new($true));try{$writer.Write($instructions)}finally{$writer.Dispose()}
        }finally{$zip.Dispose()}
    }finally{$stream.Dispose()}
    Write-Output $path
}
Write-Package (Join-Path $offlineDir '自动判读系统_v0.10.26_干净离线包.zip') $cleanFiles '自动判读系统/'
$updateFiles=@($files | Where-Object { $_.Relative -match '^(web|native|说明)/|^tools/清理运行记录.ps1$' -or $_.Relative -in @('TelemetryMonitor.exe','TelemetryMonitor.exe.config','启动服务器.cmd','本机预览.cmd','配置防火墙.ps1') })
Write-Package (Join-Path $upgradeDir '自动判读系统_v0.10.26_程序更新包.zip') $updateFiles ''
