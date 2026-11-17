"""Real local multicast reception; TTL=0 keeps fixture traffic on this host."""
import json
import secrets
import shutil
import socket
import subprocess
import tempfile
import time
from pathlib import Path
from integration_test import Client, ROOT, free_port, packet, wait, seed_send_monitor


def main():
    checks = []
    def check(ok, name):
        assert ok, name
        checks.append(name); print('PASS', name, flush=True)
    addresses = subprocess.check_output(['powershell.exe','-NoProfile','-Command', '[System.Net.NetworkInformation.NetworkInterface]::GetAllNetworkInterfaces() | Where-Object { $_.OperationalStatus -eq "Up" } | ForEach-Object { $_.GetIPProperties().UnicastAddresses } | Where-Object { $_.Address.AddressFamily -eq "InterNetwork" -and $_.Address.ToString() -ne "127.0.0.1" } | ForEach-Object { $_.Address.ToString() }']).decode('ascii').split()
    assert addresses, 'No active IPv4 network interface is available for a real multicast socket test'
    local_ip = addresses[0]; group = '239.255.42.7'
    with tempfile.TemporaryDirectory(prefix='telemetry-multicast-') as scratch:
        folder=Path(scratch);shutil.copytree(ROOT/'config',folder/'config'); seed_send_monitor(folder);shutil.copytree(ROOT/'web',folder/'web');(folder/'config/decoder.json').unlink(missing_ok=True)
        port=free_port();udp_port=free_port(socket.SOCK_DGRAM)
        process=subprocess.Popen([str(ROOT/'TelemetryMonitor.exe'),'--local','--root',str(folder),'--web-port',str(port)],stdout=subprocess.PIPE,stderr=subprocess.STDOUT)
        shared=None
        try:
            c=Client(port)
            def ready():
                try:return c.call('/api/auth/me')['setupRequired']
                except OSError:return False
            wait(ready,'multicast server');password=secrets.token_urlsafe(18);c.call('/api/auth/setup',dict(username='multicast_qa',name='组播测试',password=password));c.csrf=c.call('/api/auth/login',dict(username='multicast_qa',password=password))['csrf']
            c.call('/api/selection',dict(ids=['工仪全程/A1']))
            settings=c.call('/api/config');settings.update(BindAddress='0.0.0.0',UdpPort=udp_port,SourceIPs=[local_ip],MappingConfirmed=True,MulticastGroup='',MulticastInterface='')
            c.call('/api/config',settings)
            for invalid in ('192.168.1.1','240.1.1.1','239.999.1.1','ff02::1','bad'):
                c.call('/api/config',dict(settings,MulticastGroup=invalid),expected=400)
            c.call('/api/config',dict(settings,MulticastGroup=group,BindAddress=local_ip),expected=400)
            c.call('/api/config',dict(settings,MulticastGroup=group,MulticastInterface='0.0.0.0'),expected=400)
            check(c.call('/api/config')['MulticastGroup']=='','invalid groups, group-as-bind confusion and invalid interface inputs cannot change saved settings')
            with socket.socket(socket.AF_INET,socket.SOCK_DGRAM) as sender:
                sender.bind((local_ip,0));sender.setsockopt(socket.IPPROTO_IP,socket.IP_MULTICAST_IF,socket.inet_aton(local_ip));sender.setsockopt(socket.IPPROTO_IP,socket.IP_MULTICAST_TTL,0);sender.setsockopt(socket.IPPROTO_IP,socket.IP_MULTICAST_LOOP,1)
                c.call('/api/control',dict(action='start'));sender.sendto(packet(1),(group,udp_port));time.sleep(.2)
                check(c.call('/api/state')['received']==0,'binding only the UDP port does not receive this multicast stream without joining the group')
                c.call('/api/control',dict(action='stop'))
                c.call('/api/config',dict(settings,MulticastGroup=group,MulticastInterface='192.0.2.123'))
                response=c.call('/api/control',dict(action='start'),expected=400)
                check('本机' in response['error'] and c.call('/api/state')['mode']=='stopped','a nonexistent local interface produces a clear error and leaves reception stopped')
                settings.update(MulticastGroup=group,MulticastInterface=local_ip);c.call('/api/config',settings)
                shared=socket.socket(socket.AF_INET,socket.SOCK_DGRAM);shared.setsockopt(socket.SOL_SOCKET,socket.SO_REUSEADDR,1);shared.bind(('0.0.0.0',udp_port));shared.setsockopt(socket.IPPROTO_IP,socket.IP_ADD_MEMBERSHIP,socket.inet_aton(group)+socket.inet_aton(local_ip));shared.settimeout(3)
                c.call('/api/control',dict(action='start'));state=c.call('/api/state')
                check(state['multicastGroup']==group and state['multicastInterfaces']==[local_ip] and any('已加入组播组' in e['Message'] for e in state['events']),'startup joins the selected group on the selected local interface and exposes the actual membership')
                data=packet(2);sender.sendto(data,(group,udp_port));other=shared.recvfrom(65535)[0]
                wait(lambda:c.call('/api/state')['accepted']==1,'real multicast datagram')
                state=c.call('/api/state')
                check(state['received']==1 and state['rows'][0]['value']=='12.3456' and state['rows'][0]['hexSource']=='0x000001E240','real multicast UDP bytes reach the same SPP decoder and parameter hex output')
                check(other==data,'a second receiver using port sharing receives the same full multicast datagram')
                c.call('/api/config',settings,expected=409)
                check(True,'multicast configuration cannot be changed while receiving')
                before=state['received'];sender.sendto(packet(3),(group,udp_port+1));time.sleep(.15)
                check(c.call('/api/state')['received']==before,'joining a group does not bypass the configured UDP destination port')
                c.call('/api/control',dict(action='stop'));settings['SourceIPs']=['192.0.2.99'];c.call('/api/config',settings);c.call('/api/control',dict(action='start'))
                sender.sendto(packet(4),(group,udp_port));wait(lambda:c.call('/api/state')['received']==1,'multicast whitelist')
                state=c.call('/api/state');check(state['accepted']==0 and state['filtered']==1,'source whitelist is applied after socket reception and does not hide the received counter')
                c.call('/api/control',dict(action='stop'));settings.update(SourceIPs=[local_ip],MulticastInterface='');c.call('/api/config',settings);c.call('/api/control',dict(action='start'))
                check(local_ip in c.call('/api/state')['multicastInterfaces'],'automatic selection joins the active local IPv4 interface without requiring its IP to be typed')
                sender.sendto(packet(5),(group,udp_port));wait(lambda:c.call('/api/state')['accepted']==1,'automatic multicast reception')
                c.call('/api/control',dict(action='stop'));stopped=c.call('/api/state');sender.sendto(packet(6),(group,udp_port));time.sleep(.15)
                check(c.call('/api/state')['received']==stopped['received'] and not stopped['multicastInterfaces'],'stopping releases active membership and keeps the final statistics unchanged')
            shared.close();shared=None
            settings.update(MulticastGroup='',MulticastInterface='');c.call('/api/config',settings);c.call('/api/control',dict(action='start'))
            with socket.socket(socket.AF_INET,socket.SOCK_DGRAM) as sender:
                sender.bind((local_ip,0));sender.sendto(packet(7),(local_ip,udp_port))
            wait(lambda:c.call('/api/state')['accepted']==1,'ordinary UDP after multicast')
            check(not c.call('/api/state')['multicastInterfaces'],'clearing the group restores ordinary unicast reception with existing behavior')
            c.call('/api/control',dict(action='stop'));settings.update(MulticastGroup=group,MulticastInterface=local_ip);c.call('/api/config',settings)
            process.terminate();process.wait(timeout=6);process=subprocess.Popen([str(ROOT/'TelemetryMonitor.exe'),'--local','--root',str(folder),'--web-port',str(port)],stdout=subprocess.PIPE,stderr=subprocess.STDOUT)
            fresh=Client(port)
            def reopened():
                try:return not fresh.call('/api/auth/me')['setupRequired']
                except OSError:return False
            wait(reopened,'multicast configuration restart');fresh.csrf=fresh.call('/api/auth/login',dict(username='multicast_qa',password=password))['csrf']
            saved=fresh.call('/api/config');check(saved['MulticastGroup']==group and saved['MulticastInterface']==local_ip,'multicast address and selected interface survive a server restart')
        finally:
            if shared:shared.close()
            if process.poll() is None:process.terminate();process.wait(timeout=6)
    print('All',len(checks),'multicast integration checks passed. Fixture TTL=0; no telemetry commands were transmitted.')


if __name__=='__main__':main()
