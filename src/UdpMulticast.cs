using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace TelemetryMonitor
{
    public static class UdpMulticast
    {
        public static List<string> Join(UdpClient client, Settings settings)
        {
            var joined = new List<string>();
            if (String.IsNullOrEmpty(settings.MulticastGroup)) return joined;
            var addresses = new List<IPAddress>();
            foreach (NetworkInterface adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (adapter.OperationalStatus != OperationalStatus.Up) continue;
                foreach (UnicastIPAddressInformation address in adapter.GetIPProperties().UnicastAddresses)
                    if (address.Address.AddressFamily == AddressFamily.InterNetwork && !address.Address.Equals(IPAddress.Any) &&
                        (!String.IsNullOrEmpty(settings.MulticastInterface) || !IPAddress.IsLoopback(address.Address))) addresses.Add(address.Address);
            }
            addresses = addresses.Distinct().ToList();
            if (!String.IsNullOrEmpty(settings.MulticastInterface))
            {
                IPAddress selected = IPAddress.Parse(settings.MulticastInterface);
                if (!addresses.Contains(selected)) throw new ArgumentException("组播网卡IP“" + settings.MulticastInterface + "”不是本机已启用网卡的IPv4地址，请填写服务器网卡IP或留空自动选择");
                addresses = new List<IPAddress> { selected };
            }
            if (addresses.Count == 0) throw new ArgumentException("未发现可加入组播组的本机IPv4网卡，请检查网卡和地址配置");
            IPAddress group = IPAddress.Parse(settings.MulticastGroup);
            var errors = new List<string>();
            foreach (IPAddress local in addresses)
            {
                try { client.JoinMulticastGroup(group, local); joined.Add(local.ToString()); }
                catch (SocketException ex) { errors.Add(local + ": " + ex.SocketErrorCode); }
            }
            if (joined.Count == 0) throw new ArgumentException("加入组播组“" + settings.MulticastGroup + "”失败：" + String.Join("；", errors));
            return joined;
        }
    }
}
