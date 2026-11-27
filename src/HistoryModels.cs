using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace TelemetryMonitor
{
    public sealed class HistoryStorageConfiguration
    {
        public int SchemaVersion=1;
        public string Revision=Guid.NewGuid().ToString("N"),ArchiveDirectory="",Host="127.0.0.1",Database="telemetry_history",Username="telemetry_ingest",EncryptedPassword="";
        public int Port=5432,FlushMilliseconds=1000;
        public bool DatabaseEnabled;
        public bool? ArchiveEnabled;
        internal bool RecordingEnabled { get { return ArchiveEnabled ?? DatabaseEnabled; } }
        internal void Validate()
        {
            if(SchemaVersion!=1)throw new ArgumentException("历史存储配置版本不支持");
            if(Port<1||Port>65535||String.IsNullOrWhiteSpace(Host)||Host.Length>255)throw new ArgumentException("数据库地址或端口无效");
            if(!System.Text.RegularExpressions.Regex.IsMatch(Database??"",@"^[A-Za-z_][A-Za-z0-9_]{0,62}$")||String.IsNullOrWhiteSpace(Username)||Username.Length>63)throw new ArgumentException("数据库名或用户名无效");
            if(FlushMilliseconds<100||FlushMilliseconds>10000)throw new ArgumentException("归档刷新间隔应为100～10000毫秒");
            if(!String.IsNullOrWhiteSpace(ArchiveDirectory)){string path=Path.GetFullPath(ArchiveDirectory);if(!Path.IsPathRooted(ArchiveDirectory)||path.StartsWith(@"\\",StringComparison.Ordinal))throw new ArgumentException("历史目录须为本机磁盘的绝对路径");ArchiveDirectory=path.TrimEnd(Path.DirectorySeparatorChar);}
        }
        internal string DirectoryFor(string root){return String.IsNullOrWhiteSpace(ArchiveDirectory)?Path.Combine(root,"data","history"):ArchiveDirectory;}
        internal static string Protect(string password)
        {return Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(password??""),null,DataProtectionScope.CurrentUser));}
        internal string Password()
        {try{return EncryptedPassword.Length==0?"":Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(EncryptedPassword),null,DataProtectionScope.CurrentUser));}catch(Exception){throw new InvalidOperationException("数据库凭据无法在当前Windows账号解密，请重新填写密码并保存");}}
    }
    internal sealed class HistoryRecord
    {
        public string Id=Guid.NewGuid().ToString("N"),Kind="",Experiment="",Session="",Boot="",Configuration="",Time="",Received="",TimeBasis="";
        // Network capture provenance is retained separately from server observation time.
        public bool? CaptureTimeConfirmed;
        public string CaptureTime="",CaptureEventId="";
        public long CaptureTicks;
        public long ObservedTicks,ReceivedTicks,MonotonicTicks,Ordinal;
        public string SourceIP="",DestinationIP="",Direction="",Mode="",PacketId="",PacketName="",Status="",Reason="",PayloadBase64="",PayloadSha="";
        public int SourcePort,DestinationPort,OuterSourceId=-1,Apid=-1,Sequence=-1;
        public long CaptureFrame;
        public HistoryValue[] Samples;
        public object Data;
        internal int EstimatedBytes=1024;
        internal byte[] PayloadBytes;
        internal HistoryRecord DatabaseCopy(){var copy=(HistoryRecord)MemberwiseClone();copy.Samples=null;if(PayloadBase64.Length>0&&String.IsNullOrEmpty(PayloadSha))copy.PayloadSha=HistoryText.Hash(Convert.FromBase64String(PayloadBase64));copy.PayloadBase64="";return copy;}
    }
    internal sealed class HistoryValue
    {
        public string ParameterId,Value,ValueKind,Hex,Raw,Quality,Diagnostic;
    }
    internal sealed class HistoryConfigurationSnapshot
    {
        public object Listener,Decoder,Rules,Commands,TCBindings;
        public Parameter[] Parameters;
        public string SoftwareVersion=typeof(Monitor).Assembly.GetName().Version.ToString(3);
    }
    internal sealed class HistoryArchiveSegment
    {
        public string Id,File,FirstTime,LastTime,Hash;
        public int Count;
        public long Bytes;
    }
    internal sealed class HistoryArchiveCursor
    {public Dictionary<string,long> Offsets=new Dictionary<string,long>(StringComparer.OrdinalIgnoreCase);public long Segments,Events,Samples;}
    internal sealed class HistoryExperiment
    {public string Id=Guid.NewGuid().ToString("N"),Name="",StartedAt="",EndedAt="",Notes="";}
    internal static class HistoryJson
    {
        internal static string Encode(object value){return new System.Web.Script.Serialization.JavaScriptSerializer{MaxJsonLength=Int32.MaxValue,RecursionLimit=128}.Serialize(value);}
        internal static T Decode<T>(string value){return new System.Web.Script.Serialization.JavaScriptSerializer{MaxJsonLength=Int32.MaxValue,RecursionLimit=128}.Deserialize<T>(value);}
    }
    internal static class HistoryText
    {
        internal static string Utc(DateTime time){return time.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'",CultureInfo.InvariantCulture);}
        internal static string Hash(byte[] bytes){using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-","").ToLowerInvariant();}
        internal static string Hash(string text){return Hash(Encoding.UTF8.GetBytes(text));}
    }
}
