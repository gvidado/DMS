param([string]$Root = '')
$ErrorActionPreference = 'Stop'
if (!$Root) { $Root = Split-Path -Parent $PSScriptRoot }
$applicationRoot = (Resolve-Path -LiteralPath $Root).Path.TrimEnd('\')
$executable = Join-Path $applicationRoot 'TelemetryMonitor.exe'
if (!(Test-Path -LiteralPath $executable -PathType Leaf) -or !(Test-Path -LiteralPath (Join-Path $applicationRoot 'config\server.json'))) { throw 'Not a TelemetryMonitor application directory.' }
$runningApplications = @(Get-CimInstance Win32_Process -Filter "Name='TelemetryMonitor.exe'" | Where-Object { $_.ExecutablePath -eq $executable })
if ($runningApplications.Count -gt 0) { throw 'Stop this application before clearing records. No files were changed.' }
$dataRoot = [IO.Path]::GetFullPath((Join-Path $applicationRoot 'data'))
$logsRoot = [IO.Path]::GetFullPath((Join-Path $applicationRoot 'logs'))
$backupRoot = Join-Path $applicationRoot ('备份\运行记录清理_'+(Get-Date -Format 'yyyyMMdd_HHmmss_ffff'))
$targets = [Collections.Generic.List[object]]::new()
foreach ($parent in @($dataRoot,$logsRoot)) {
    if (!(Test-Path -LiteralPath $parent)) { continue }
    foreach ($item in Get-ChildItem -LiteralPath $parent -Force) {
        if ($parent -eq $dataRoot -and $item.Name -in @('accounts.json','accounts.json.bak','pending-configuration')) { continue }
        $absolute = [IO.Path]::GetFullPath($item.FullName)
        if (!$absolute.StartsWith(($parent+'\'),[StringComparison]::OrdinalIgnoreCase) -or $item.Attributes.HasFlag([IO.FileAttributes]::ReparsePoint)) { throw 'Unexpected path or directory link. No files were changed.' }
        if ($item.PSIsContainer -and @(Get-ChildItem -LiteralPath $absolute -Recurse -Force | Where-Object { $_.Attributes.HasFlag([IO.FileAttributes]::ReparsePoint) }).Count -gt 0) { throw 'A record folder contains a directory link. No files were changed.' }
        $targets.Add([pscustomobject]@{ Path=$absolute; Parent=$parent; Name=$item.Name })
    }
}
# Back up every target before removing any target.
foreach ($target in $targets) {
    $subfolder = if ($target.Parent -eq $dataRoot) { 'data' } else { 'logs' }
    $destination = Join-Path $backupRoot $subfolder
    New-Item -ItemType Directory -Path $destination -Force | Out-Null
    Copy-Item -LiteralPath $target.Path -Destination (Join-Path $destination $target.Name) -Recurse -Force
}
foreach ($target in $targets) {
    $absolute = [IO.Path]::GetFullPath($target.Path)
    if (!$absolute.StartsWith(($target.Parent+'\'),[StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe cleanup path.' }
    Remove-Item -LiteralPath $absolute -Recurse -Force
}
Write-Output ('Cleared runtime record items: '+$targets.Count)
Write-Output ('Accounts and configuration retained. Backup: '+$backupRoot)
