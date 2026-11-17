param([Parameter(Mandatory=$true)][ValidateRange(1,65535)][int]$UdpPort, [ValidateRange(1024,65535)][int]$WebPort = 8080)
$ErrorActionPreference = 'Stop'
# Run manually as Administrator on the deployment server after confirming the real UDP port.
New-NetFirewallRule -DisplayName 'Telemetry Monitor - Web LAN' -Direction Inbound -Action Allow -Protocol TCP -LocalPort $WebPort -RemoteAddress LocalSubnet
New-NetFirewallRule -DisplayName 'Telemetry Monitor - UDP LAN' -Direction Inbound -Action Allow -Protocol UDP -LocalPort $UdpPort -RemoteAddress LocalSubnet
Write-Host 'Created two inbound rules restricted to LocalSubnet. Existing firewall rules were not modified.'
