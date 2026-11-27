param([string]$ServiceName = 'postgresql-x64-18')
$ErrorActionPreference = 'Stop'
try {
    $service = Get-Service -Name $ServiceName
    if ($service.Status -ne 'Running') { Start-Service -Name $ServiceName }
    (Get-Service -Name $ServiceName) | Select-Object Name, Status, StartType
    Write-Host 'PostgreSQL service is running. The existing database has not been changed.'
} catch {
    Write-Host ('Cannot start PostgreSQL: ' + $_.Exception.Message)
    Write-Host 'Open PowerShell as Windows Administrator and run this script again.'
    exit 1
}
