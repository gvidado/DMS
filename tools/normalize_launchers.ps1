$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$launchers = Get-ChildItem -LiteralPath $projectRoot -Filter '*.cmd' -File
foreach ($launcher in $launchers) {
    $content = [System.IO.File]::ReadAllText($launcher.FullName, [System.Text.Encoding]::UTF8)
    if ($content.ToCharArray() | Where-Object { [int]$_ -gt 127 }) {
        throw ('Launcher content must be ASCII: ' + $launcher.Name)
    }
    $normalized = [System.Text.RegularExpressions.Regex]::Replace($content, '\r?\n', "`r`n")
    [System.IO.File]::WriteAllText($launcher.FullName, $normalized, [System.Text.Encoding]::ASCII)
}
