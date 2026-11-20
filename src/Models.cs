using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace TelemetryMonitor
{
    public sealed class Settings
    {
        public int WebPort = 8080;
        public string WebHost = "+";
        public string BindAddress = "0.0.0.0";
        public int UdpPort = 0;
        public string MulticastGroup = "";
        public string MulticastInterface = "";
        public string[] SourceIPs = new[] { "192.168.1.113" };
        public int FramePeriodMs = 160;
        public long DataTimeoutMs;
        public bool EnablePacketTimeoutAlarm = false;
        // Retained only to migrate older configurations; reception does not use a frame schedule.
        public int OfflineFrames = 100;
        public int LossAlarmFrames = 20;
        public bool EnableLossAlarm = false;
        public int ExpectedDatagramLength = 1030;
        public int SppOffset = -1;
        public bool StrictLength = true;
        public int[] AllowedApids = new[] { 8, 9, 10 };
        public int ParameterApid = 8;
        public int PayloadOffset = 16;
        public int SourceAddress = 4100;
        public int ServiceType = 3;
        public int ServiceSubtype = 25;
        public string ChecksumMode = SppChecksum.Mode;
        public int ChecksumStart = 0;
        public bool MappingConfirmed = false;
        public string Profile = "全程";
        public string BitOrder = "msb0";
        public string ByteOrder = "big";
        public string CrossByteOrder = "firstMsb";

        public void Validate()
        {
            // Older JSON fields remain readable, but cannot override the fixed rule.
            ChecksumMode = SppChecksum.Mode; ChecksumStart = 0;
            if (WebPort < 1024 || WebPort > 65535) throw new ArgumentException("网页端口必须在1024～65535之间");
            if (UdpPort < 0 || UdpPort > 65535) throw new ArgumentException("UDP端口无效");
            System.Net.IPAddress ip;
            if (!System.Net.IPAddress.TryParse(BindAddress, out ip) || ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) throw new ArgumentException("监听地址必须是IPv4地址");
            if (ip.GetAddressBytes()[0] >= 224) throw new ArgumentException("绑定地址应为0.0.0.0或本机IPv4，组播目的地址请填写在组播地址栏");
            MulticastGroup = (MulticastGroup ?? "").Trim(); MulticastInterface = (MulticastInterface ?? "").Trim();
            if (MulticastGroup.Length > 0)
            {
                if (!System.Net.IPAddress.TryParse(MulticastGroup, out ip) || ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork || ip.GetAddressBytes()[0] < 224 || ip.GetAddressBytes()[0] > 239)
                    throw new ArgumentException("组播地址必须是224.0.0.0～239.255.255.255范围内的IPv4地址");
                if (BindAddress != "0.0.0.0") throw new ArgumentException("组播接收请将本机绑定地址设为0.0.0.0，在组播网卡IP栏指定本机网卡");
            }
            if (MulticastInterface.Length > 0 && (!System.Net.IPAddress.TryParse(MulticastInterface, out ip) || ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork || ip.Equals(System.Net.IPAddress.Any) || ip.GetAddressBytes()[0] >= 224))
                throw new ArgumentException("组播网卡IP应填写本机网卡的IPv4地址，留空自动选择");
            if (SourceIPs == null || SourceIPs.Length < 1 || SourceIPs.Length > 16) throw new ArgumentException("至少配置一个来源IP，最多16个");
            foreach (string s in SourceIPs) if (!System.Net.IPAddress.TryParse(s, out ip) || ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) throw new ArgumentException("来源IP无效：" + s);
            if (FramePeriodMs < 10 || FramePeriodMs > 60000 || OfflineFrames < 1 || OfflineFrames > 100000 || LossAlarmFrames < 1) throw new ArgumentException("参考周期或超时配置无效");
            if (DataTimeoutMs == 0) DataTimeoutMs = (long)FramePeriodMs * OfflineFrames;
            if (DataTimeoutMs < 1 || DataTimeoutMs > 6000000000L) throw new ArgumentException("无有效数据超时应为1～6000000000毫秒");
            if (ExpectedDatagramLength < 0 || ExpectedDatagramLength > 65507 || SppOffset < -1 || SppOffset > 4096 || PayloadOffset < 6 || PayloadOffset > 4096 || ChecksumStart < 0 || ChecksumStart > 4096) throw new ArgumentException("协议偏移或长度无效");
            if (AllowedApids == null || AllowedApids.Length == 0 || AllowedApids.Length > 32) throw new ArgumentException("至少配置一个APID");
            foreach (int a in AllowedApids) if (a < 0 || a > 2047) throw new ArgumentException("APID必须在0～2047之间");
            if (Array.IndexOf(AllowedApids, ParameterApid) < 0) throw new ArgumentException("参数APID必须在允许的APID列表中");
            if (SourceAddress < 0 || SourceAddress > 65535 || ServiceType < 0 || ServiceType > 255 || ServiceSubtype < 0 || ServiceSubtype > 255) throw new ArgumentException("SPP源地址、业务类型或业务子类型越界");
            if (Array.IndexOf(new[] { "全程", "RVD200外", "RVD200内", "IMU标定" }, Profile) < 0) throw new ArgumentException("不支持的参数布局");
            if (BitOrder != "lsb0" && BitOrder != "msb0") throw new ArgumentException("位编号方式无效");
        }
    }

    public sealed class Parameter
    {
        public string Id;
        public string Code;
        public string TelemetryNamespace = "";
        public string Reference { get { return TelemetryReferences.Name(Code,TelemetryNamespace); } }
        public string ValueReference { get { return Reference.Length == 0 ? "" : Reference + ".value"; } }
        public string Name;
        public string Group;
        public int Offset;
        public int Length;
        public int BitStart = -1;
        public int BitCount;
        public string Type;
        public double Scale = 1;
        public double Bias;
        public string Unit;
        public int Precision = 6;
        public string Format;
        public string Note;
        public Dictionary<string, string> Enums = new Dictionary<string, string>();
        public bool Supported = true;
        public int SourceRow;
        public string SourceId;
        public int SourceSheetIndex;
        public string Position;
        public string Diagnostic;
        public bool CrossByte;
        public string PacketId;
        public string PacketName;
        public bool PacketEnabled = true;
        public List<EnumRange> EnumRanges = new List<EnumRange>();
        internal Parameter CopyForMapping()
        {
            var copy=(Parameter)MemberwiseClone();
            copy.Enums=Enums==null?null:new Dictionary<string,string>(Enums);
            copy.EnumRanges=EnumRanges==null?null:EnumRanges.Select(r=>r==null?null:new EnumRange{Min=r.Min,Max=r.Max,Text=r.Text}).ToList();return copy;
        }
    }

    public sealed class Reading
    {
        internal long DisplayRevision;
        public string PacketEventId="";
        public string Id;
        public string Value;
        public object EngineeringValue;
        public string EngineeringText;
        public string EngineeringKind;
        public string SourceIP;
        public int SourcePort;
        public string Raw;
        public string HexSource;
        public string Status;
        public DateTime Time;
        public int Sequence;
        public string Diagnostic;
    }

    public sealed class Account
    {
        public string Username;
        public string DisplayName;
        public string Salt;
        public string PasswordHash;
        public bool Admin;
        public List<string> Selected = new List<string>();
        public ParameterDisplayConfiguration DisplayLayout;
        public CommandPanelConfiguration CommandPanelLayout;
    }

    public sealed class Session
    {
        public string Token;
        public string Csrf;
        public string Username;
        public DateTime Expires;
        public int Streams;
    }

    public sealed class Notice
    {
        public long Id;
        public string Time;
        public string Level;
        public string Message;
    }

    public sealed class SourceState
    {
        public string IP;
        public long Received;
        public long Accepted;
        public long Duplicates;
        public long Lost;
        public long OutOfOrder;
        public long Invalid;
        public DateTime LastReceived;
        public DateTime LastValid;
        public bool OfflineLatched;
        public bool OfflineConditionActive;
        public bool LossLatched;
    }

    public static class JsonFile
    {
        // Configurations and generated responses grow with the parameter/rule catalog.
        // Bound incoming requests at the HTTP endpoint, rather than truncating valid data here.
        private static JavaScriptSerializer Serializer()
        {
            return new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue, RecursionLimit = 128 };
        }
        public static string Encode(object value)
        {
            return Serializer().Serialize(value);
        }
        public static T Decode<T>(string text)
        {
            return Serializer().Deserialize<T>(text);
        }
        public static T Load<T>(string path) { return Decode<T>(File.ReadAllText(path, Encoding.UTF8)); }
        public static void Save(string path, object value)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, Encode(value), new UTF8Encoding(false));
            if (File.Exists(path)) File.Replace(tmp, path, path + ".bak");
            else File.Move(tmp, path);
        }
    }
}
