param([string]$ReleaseExecutable = '')
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$versionText = [IO.File]::ReadAllText((Join-Path $projectRoot 'src/AssemblyInfo.cs'))
$releaseVersion = [regex]::Match($versionText, 'AssemblyInformationalVersion\("([0-9]+\.[0-9]+\.[0-9]+)"\)').Groups[1].Value
if (!$releaseVersion) { throw 'Cannot read release version' }
& (Join-Path $PSScriptRoot 'normalize_launchers.ps1')
$releaseRoot = Split-Path -Parent $projectRoot
$offlineDir = Join-Path $releaseRoot '离线包'
$upgradeDir = Join-Path $releaseRoot '升级包'
New-Item -ItemType Directory -Path $offlineDir, $upgradeDir -Force | Out-Null
if (!$ReleaseExecutable) { $ReleaseExecutable = Join-Path $projectRoot 'TelemetryMonitor.exe' }
$ReleaseExecutable = (Get-Item -LiteralPath $ReleaseExecutable).FullName
if ([Diagnostics.FileVersionInfo]::GetVersionInfo($ReleaseExecutable).FileVersion -ne ($releaseVersion+'.0')) { throw ('Expected version '+$releaseVersion+'.0') }
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$files = [Collections.Generic.List[object]]::new()
function Select-File([string]$path, [string]$relative) {
    if (Test-Path -LiteralPath $path -PathType Leaf) { $files.Add([pscustomobject]@{ File = (Get-Item -LiteralPath $path); Relative = $relative.Replace('\','/') }) }
}
foreach ($folder in @('web','native','database')) {
    $base = Join-Path $projectRoot $folder
    if (!(Test-Path -LiteralPath $base)) { continue }
    Get-ChildItem -LiteralPath $base -File -Recurse | Where-Object { $_.Extension -notin @('.pyc','.bak','.tmp') -and $_.FullName -notlike '*\__pycache__\*' -and $_.FullName -notlike '*\历史根目录文件\*' } | ForEach-Object { Select-File $_.FullName $_.FullName.Substring($projectRoot.Length+1) }
}
foreach ($name in @('TelemetryMonitor.exe.config','启动服务器.cmd','本机预览.cmd','配置防火墙.ps1')) { Select-File (Join-Path $projectRoot $name) $name }
foreach ($name in @('指令面板与自由布局说明.md','参数画面配置说明.md','V0.10.26更新说明.md','V0.10.25更新说明.md','V0.10.24更新说明.md','V0.10.23更新说明.md','V0.10.22更新说明.md','V0.10.21更新说明.md','V0.10.20更新说明.md','V0.10.19更新说明.md','V0.10.18更新说明.md','V0.10.17更新说明.md','V0.10.16接收计数重同步修复说明.md','V0.10.15XML指令导入更新说明.md','V0.10.13更新说明.md','V0.10.12更新说明.md','V0.10.11更新说明.md','V0.10.10更新说明.md')) { Select-File (Join-Path $projectRoot ('说明/'+$name)) ('说明/'+$name) }
foreach ($name in @('清理运行记录.ps1','初始化历史数据库.ps1','启动PostgreSQL18.ps1')) {
    Select-File (Join-Path $projectRoot ('tools/'+$name)) ('tools/'+$name)
}
Select-File $ReleaseExecutable 'TelemetryMonitor.exe'
$currentNotes = '说明/V'+$releaseVersion+'更新说明.md'
if (!$files.Exists([Predicate[object]]{param($item) $item.Relative -eq $currentNotes})) { Select-File (Join-Path $projectRoot $currentNotes) $currentNotes }
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
$instructions = '数据判读系统 V'+$releaseVersion+"`r`n`r`n"+[IO.File]::ReadAllText((Join-Path $projectRoot $currentNotes))+"`r`n`r`n"+@'
升级包：停止旧程序，覆盖本包文件，保留原config、data和logs；重新启动后按Ctrl+F5刷新网页。

干净离线包：解压到新的空目录。默认组播224.10.2.24、UDP端口30008、来源192.168.1.111～192.168.1.113。解码、SPP、判读、指令和序列配置均为空，不含账号或现场数据；首次在本机网页创建管理员。

历史库使用 PostgreSQL。新部署默认关闭全量采集和数据库写入。可在网页的「系统配置 → 历史存储配置」启用。升级保留已有独立归档目录和数据库。
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
Write-Package (Join-Path $offlineDir ('自动判读系统_v'+$releaseVersion+'_干净离线包.zip')) $cleanFiles '自动判读系统/'
$updateFiles=@($files | Where-Object { $_.Relative -match '^(web|native|database|说明)/|^tools/(清理运行记录|初始化历史数据库|启动PostgreSQL18)\.ps1$' -or $_.Relative -in @('TelemetryMonitor.exe','TelemetryMonitor.exe.config','启动服务器.cmd','本机预览.cmd','配置防火墙.ps1') })
Write-Package (Join-Path $upgradeDir ('自动判读系统_v'+$releaseVersion+'_程序更新包.zip')) $updateFiles ''
