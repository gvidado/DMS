param([string]$OutputPath = "")
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
& (Join-Path $projectRoot 'tools\normalize_launchers.ps1')
$pluginSync = Join-Path $projectRoot 'dsh-plugin\scripts\sync-system.ps1'
if (Test-Path -LiteralPath $pluginSync) { & $pluginSync -SystemRoot $projectRoot -CheckOnly }
if ([string]::IsNullOrEmpty($OutputPath)) { $OutputPath = Join-Path $projectRoot 'TelemetryMonitor.exe' }
$OutputPath = [System.IO.Path]::GetFullPath($OutputPath)
New-Item -ItemType Directory -Path (Split-Path -Parent $OutputPath) -Force | Out-Null
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) {
    throw '.NET Framework 4.x C# compiler not found. Enable .NET Framework 4.5 features first.'
}
$sourceFiles = @(Get-ChildItem -LiteralPath (Join-Path $projectRoot 'src') -Filter '*.cs' | ForEach-Object { $_.FullName })
& $compiler /nologo /target:exe /platform:anycpu /optimize+ /codepage:65001 /r:System.dll /r:System.Core.dll /r:System.Security.dll /r:System.Web.Extensions.dll /r:System.Xml.dll /r:System.Xml.Linq.dll /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll "/out:$OutputPath" $sourceFiles
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
Write-Host 'Build succeeded: TelemetryMonitor.exe'
& $OutputPath --self-test
if ($LASTEXITCODE -ne 0) { throw 'Protocol self-test failed' }
