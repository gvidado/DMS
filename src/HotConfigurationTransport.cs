using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace TelemetryMonitor
{
    public sealed partial class SendMonitor
    {
        internal object ConfigurationGate {get{return gate;}}
        internal SendMonitorSettings ConfigurationCopy(){lock(gate)return Clone(settings);}
        internal void ApplyHotSettings(SendMonitorSettings value)
        {
            value=Clone(value);value.Validate();SendMonitorSettings before;bool active;
            lock(gate){before=Clone(settings);active=Active;if(CanonicalJson.Encode(before)==CanonicalJson.Encode(value))return;}
            if(active)Stop();
            try{lock(gate)settings=value;if(active)Start(true);}
            catch{lock(gate)settings=before;if(active)Start(true);throw;}
        }
    }
    internal sealed partial class CommandTransmitter
    {
        internal void UseEditorRevision(string value){lock(gate)revision=value;}
        internal void AdoptConfiguration(CommandTransmitter source)
        {lock(gate){configuration=Clone(source.configuration);revision=Guid.NewGuid().ToString("N");error=source.error;plans.Clear();}}
    }
    public sealed partial class ObservedCommands
    {
        internal object ConfigurationGate {get{return gate;}}
        internal void AdoptConfiguration(ObservedCommands source)
        {
            lock(gate){configuration=Serializer().Deserialize<ObservedCommandConfiguration>(Serializer().Serialize(source.configuration));index.Clear();Validate();ready=true;error="";revision++;Stamp();}
        }
    }
    public sealed partial class Monitor
    {
        internal bool ConfigurationEditing;
        internal bool ConfigurationMonitoringActive {get{lock(Gate)return Listening||Demo||stoppingTogether||Outgoing.IsActive;}}
        internal volatile bool ApplyingConfiguration;
        private Settings hotSocketSettings;
        private static string SocketDefinition(Settings value){return CanonicalJson.Encode(new{value.BindAddress,value.UdpPort,value.MulticastGroup,value.MulticastInterface});}
        private static Settings CopySettings(Settings value){return JsonFile.Decode<Settings>(JsonFile.Encode(value));}
        internal void ApplyHotSocket(Settings incoming)
        {
            lock(Gate)
            {
                if(!Listening){hotSocketSettings=null;return;}
                if(incoming.UdpPort==0)throw new ArgumentException("监听运行中不能将UDP端口设置为0");
                var before=hotSocketSettings??CopySettings(Config);if(SocketDefinition(before)==SocketDefinition(incoming))return;
                bool sameEndpoint=before.BindAddress==incoming.BindAddress&&before.UdpPort==incoming.UdpPort;
                UdpClient previous=socket,pending=null;bool closed=false;
                try
                {
                    if(sameEndpoint){socket=null;previous.Close();closed=true;}
                    pending=OpenHotSocket(incoming);var joined=UdpMulticast.Join(pending,incoming);socket=pending;hotSocketSettings=CopySettings(incoming);multicastInterfaces=joined;
                    if(!closed){previous.Close();closed=true;}var client=pending;receiver=new Thread(()=>Receive(client)){IsBackground=true,Name="UDP receiver"};receiver.Start();
                }
                catch
                {
                    if(pending!=null)pending.Close();
                    if(closed){var restored=OpenHotSocket(before);multicastInterfaces=UdpMulticast.Join(restored,before);socket=restored;receiver=new Thread(()=>Receive(restored)){IsBackground=true,Name="UDP receiver"};receiver.Start();}
                    else socket=previous;
                    hotSocketSettings=before;throw;
                }
            }
        }
        private static UdpClient OpenHotSocket(Settings value)
        {
            var client=new UdpClient(AddressFamily.InterNetwork);
            try{bool multicast=!String.IsNullOrEmpty(value.MulticastGroup);client.ExclusiveAddressUse=!multicast;if(multicast)client.Client.SetSocketOption(SocketOptionLevel.Socket,SocketOptionName.ReuseAddress,true);client.Client.ReceiveBufferSize=4*1024*1024;client.Client.ReceiveTimeout=0;client.Client.Bind(new IPEndPoint(IPAddress.Parse(value.BindAddress),value.UdpPort));return client;}
            catch{client.Close();throw;}
        }
    }
}
