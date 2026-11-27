param([string]$PgBin = '', [int]$Port = 5432, [string]$Administrator = 'postgres')
$ErrorActionPreference = 'Stop'
if (!$PgBin) {
    foreach ($candidate in @('D:\Program Files\PostgreSQL\18\bin', 'C:\Program Files\PostgreSQL\18\bin')) {
        if (Test-Path -LiteralPath (Join-Path $candidate 'psql.exe')) { $PgBin = $candidate; break }
    }
}
$psql = Join-Path $PgBin 'psql.exe'
if (!(Test-Path -LiteralPath $psql)) { throw 'Cannot find psql.exe. Specify -PgBin with the installed PostgreSQL bin directory.' }
if ($Port -lt 1 -or $Port -gt 65535) { throw 'Invalid PostgreSQL port.' }
$projectRoot = Split-Path -Parent $PSScriptRoot
$sql = Join-Path $projectRoot 'database\initialize-history.sql'
Write-Host 'Connect to the LOCAL PostgreSQL server only (127.0.0.1).'
Write-Host 'First enter the existing postgres administrator password.'
Write-Host 'If creating a new database, psql then asks twice for a NEW application password.'
Write-Host 'Passwords are entered locally without being echoed or saved in this script.'
& $psql -X -W -h 127.0.0.1 -p $Port -U $Administrator -d postgres -v ON_ERROR_STOP=1 -f $sql
if ($LASTEXITCODE -ne 0) { throw 'Database initialization did not finish. Read the PostgreSQL error above; existing databases were not reset.' }
Write-Host 'Next: open System Configuration > History Storage in the web application.'
Write-Host 'Use 127.0.0.1, port 5432 (or your selected port), telemetry_history, telemetry_ingest.'
Write-Host 'Recommended permanent archive directory: D:\TelemetryHistory.'
Write-Host 'Save the application password there, then check/initialize history tables.'
