using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace TelemetryMonitor
{
    public sealed class ObservedCommand
    {
        public long Number;
        public string Name, DestinationIP, PayloadHex, FirstSeenUtc;
        public int Length;
        public bool STC;
        public int InterfaceBytes;
        public string FormatIssue="";
        public List<string> DestinationIPs = new List<string>();
        public List<ObservedCommandAlias> Aliases = new List<ObservedCommandAlias>();
        public List<CommandCategory> Categories = new List<CommandCategory>();
        public bool MatchesNumber(long number){return Number==number || Aliases.Any(a=>a.Number==number);}
    }
    public sealed class ObservedCommandAlias { public long Number; public string Name; }
    internal sealed class CommandDisplay
    {public bool Matched;public string Code="",Name="";}
    public sealed class ObservedCommandConfiguration
    {
        public int SchemaVersion = 1;
        public int InterfaceWidthRule = 4;
        public string SourceIP = "192.168.1.16";
        public string IdentityRule = "udp-payload";
        public long NextNumber = 1;
        public List<ObservedCommand> Commands = new List<ObservedCommand>();
        public Dictionary<string,long> NextCategoryNumbers = new Dictionary<string,long>{{"BC",1},{"PSS",1},{"KC",1}};
    }
    // Persistent payload identities; captured transmission records keep each actual destination.
    public sealed partial class ObservedCommands
    {
        private readonly object gate = new object();
        private readonly string path;
        private readonly Dictionary<string, List<ObservedCommand>> index = new Dictionary<string, List<ObservedCommand>>();
        private ObservedCommandConfiguration configuration = new ObservedCommandConfiguration();
        private bool ready;
        private string error = "";
        private long revision, savedLength;
        private DateTime savedTime;
        private int migratedInterfaceCommands;
        public string Error { get { lock (gate) return error; } }
        internal string BindingSource { get { lock(gate) return ready ? configuration.SourceIP : ""; } }
        internal long DisplayRevision {get{lock(gate)return revision;}}
        internal CommandDisplay Describe(string source,string destination,byte[] payload)
        {
            lock(gate)
            {
                if(!ready || source!=configuration.SourceIP)return new CommandDisplay();
                var match=MatchCommand(payload);var entries=match.Entries.Where(e=>match.BCPriority||e.DestinationIPs.Contains(destination)).OrderBy(e=>e.Number).ToArray();
                if(entries.Length==0)return new CommandDisplay();
                var codes=entries.SelectMany(entry=>entry.Categories.Where(category=>match.BCPriority?category.Kind=="BC":CategoryMatches(entry,category,destination)).Select(category=>category.Code)).Distinct().ToArray();
                return new CommandDisplay{Matched=true,Code=String.Join(" / ",codes),Name=String.Join(" / ",entries.Select(entry=>entry.Name).Distinct())};
            }
        }
        internal ObservedCommand[] BindingEntries(){lock(gate){if(!ready)throw new InvalidOperationException(error);return configuration.Commands.ToArray();}}
        public ObservedCommands(string root)
        {
            path = Path.Combine(root, "config", "observed-commands.json");
            try
            {
                if (File.Exists(path))
                {
                    Stamp();
                    var serializer = Serializer();
                    var top = serializer.Deserialize<Dictionary<string,object>>(File.ReadAllText(path,Encoding.UTF8));
                    configuration = serializer.ConvertToType<ObservedCommandConfiguration>(top);
                    if(Validate(top.ContainsKey("InterfaceWidthRule")?configuration.InterfaceWidthRule:0))SaveFile(true);
                }
                else SaveFile(false);
                ready = true;
            }
            catch (Exception ex) { configuration = new ObservedCommandConfiguration(); index.Clear(); error = "指令配置读取/创建失败，原文件保留：" + ex.Message; }
        }
        private static JavaScriptSerializer Serializer() { return new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue, RecursionLimit = 64 }; }
        private static string IPv4(string text)
        {
            IPAddress ip;
            if (!IPAddress.TryParse(text, out ip) || ip.AddressFamily != AddressFamily.InterNetwork || ip.Equals(IPAddress.Any)) throw new ArgumentException("指令配置中的IP地址无效");
            return ip.ToString();
        }
        private static string Hex(byte[] data) { return BitConverter.ToString(data).Replace('-', ' '); }
        private static string Key(byte[] payload,int storedInterfaceBytes=0)
        {
            using (SHA256 digest = SHA256.Create()) return Convert.ToBase64String(digest.ComputeHash(CommandEnvelope.ObservedIdentity(payload,storedInterfaceBytes)));
        }
        internal ObservedCommand[] MatchingEntries(byte[] payload)
        {
            return MatchCommand(payload).Entries;
        }
        private void AddIndex(ObservedCommand entry, byte[] payload)
        {
            string key = Key(payload,entry.InterfaceBytes); List<ObservedCommand> bucket;
            if (!index.TryGetValue(key, out bucket)) index[key] = bucket = new List<ObservedCommand>();
            if (bucket.Any(p => p.PayloadHex == entry.PayloadHex)) throw new ArgumentException("指令配置包含重复的UDP载荷：" + entry.Name);
            bucket.Add(entry);
        }
        private bool Validate(int previousWidthRule=4)
        {
            if (configuration == null || configuration.SchemaVersion != 1 || (configuration.IdentityRule != "destination-ip-and-udp-payload" && configuration.IdentityRule != "udp-payload") || configuration.Commands == null)
                throw new ArgumentException("指令配置版本或结构不受支持");
            if(previousWidthRule<0||previousWidthRule>4)throw new ArgumentException("指令接口长度规则版本不受支持");
            bool changed=configuration.IdentityRule!="udp-payload"||previousWidthRule!=4;configuration.InterfaceWidthRule=4;
            configuration.SourceIP = IPv4(configuration.SourceIP);
            if (IPAddress.Parse(configuration.SourceIP).GetAddressBytes()[0] >= 224) throw new ArgumentException("指令配置来源IP应为单播地址");
            var numbers = new HashSet<long>();
            var payloads = new Dictionary<long,byte[]>();
            long maximum = 0;
            foreach (ObservedCommand entry in configuration.Commands)
            {
                if (entry == null || entry.Number < 1 || entry.Number == Int64.MaxValue || !numbers.Add(entry.Number)) throw new ArgumentException("指令编号无效或重复");
                if (String.IsNullOrWhiteSpace(entry.Name)) entry.Name = "指令" + entry.Number.ToString(CultureInfo.InvariantCulture);
                if (entry.Name.Length > 256) throw new ArgumentException("指令名称不能超过256个字符");
                entry.DestinationIP = IPv4(entry.DestinationIP);
                if(entry.DestinationIPs==null || entry.DestinationIPs.Count==0){entry.DestinationIPs=new List<string>{entry.DestinationIP};changed=true;}
                entry.DestinationIPs=entry.DestinationIPs.Select(IPv4).Distinct().ToList();
                if(!entry.DestinationIPs.Contains(entry.DestinationIP)){entry.DestinationIPs.Insert(0,entry.DestinationIP);changed=true;}
                if(entry.Aliases==null)entry.Aliases=new List<ObservedCommandAlias>();
                if(entry.Categories==null)entry.Categories=new List<CommandCategory>();
                foreach(var alias in entry.Aliases)
                {if(alias==null || alias.Number<1 || alias.Number==Int64.MaxValue || !numbers.Add(alias.Number) || String.IsNullOrWhiteSpace(alias.Name) || alias.Name.Length>256)throw new ArgumentException("合并指令的历史编号或名称无效、重复");maximum=Math.Max(maximum,alias.Number);}
                if (entry.PayloadHex == null) throw new ArgumentException("指令配置缺少完整UDP载荷");
                byte[] payload = String.IsNullOrWhiteSpace(entry.PayloadHex) ? new byte[0] : PacketCodec.Hex(entry.PayloadHex);
                entry.PayloadHex = Hex(payload); entry.Length = payload.Length;
                if(entry.InterfaceBytes!=0&&entry.InterfaceBytes!=2&&entry.InterfaceBytes!=4)throw new ArgumentException("指令接口长度记录无效");
                if(previousWidthRule!=4 && CommandEnvelope.DataOffset(payload,2)>=0)
                {
                    int function=payload[20]|payload[21]<<8;
                    int previous=entry.InterfaceBytes!=0?entry.InterfaceBytes:previousWidthRule==3||entry.STC&&previousWidthRule==0?4:function==0x1553||function==0xDD&&previousWidthRule>=2?4:2;
                    if(CommandEnvelope.Valid(payload,previous))
                    {
                        string issue;byte[] upgraded=CommandEnvelope.UpgradeLegacySTC(payload,out issue,previous);
                        if(!Object.ReferenceEquals(upgraded,payload))migratedInterfaceCommands++;
                        payload=upgraded;entry.PayloadHex=Hex(payload);entry.Length=payload.Length;entry.FormatIssue=issue;
                        entry.InterfaceBytes=issue.Length>0?previous:CommandEnvelope.InterfaceTypeBytes((ushort)function);
                    }
                    else if(entry.InterfaceBytes==0)entry.InterfaceBytes=previous;
                }
                if(entry.STC && (!CommandEnvelope.Valid(payload,entry.InterfaceBytes) || entry.Categories.Count==0))throw new ArgumentException("STC指令必须有有效的EB90封装及分类编号");
                payloads.Add(entry.Number,payload);
                maximum = Math.Max(maximum, entry.Number);
            }
            var merged=new List<ObservedCommand>();var complete=new Dictionary<string,ObservedCommand>();
            foreach(var entry in configuration.Commands.OrderBy(e=>e.Number))
            {
                ObservedCommand original;
                if(!complete.TryGetValue(entry.PayloadHex,out original)){AddIndex(entry,payloads[entry.Number]);merged.Add(entry);complete.Add(entry.PayloadHex,entry);continue;}
                original.DestinationIPs=original.DestinationIPs.Concat(entry.DestinationIPs).Distinct().ToList();
                original.Aliases.Add(new ObservedCommandAlias{Number=entry.Number,Name=entry.Name});original.Aliases.AddRange(entry.Aliases);changed=true;
                original.Categories.AddRange(entry.Categories);
            }
            configuration.Commands=merged;configuration.IdentityRule="udp-payload";
            configuration.NextNumber = Math.Max(configuration.NextNumber, maximum + 1);
            changed |= NormalizeCategories(configuration);
            return changed;
        }
        private void Stamp()
        { savedLength = new FileInfo(path).Length; savedTime = File.GetLastWriteTimeUtc(path); }
        private static void WriteConfiguration(TextWriter writer, ObservedCommand[] entries, string sourceIP, long nextNumber,Dictionary<string,long> categoryNumbers)
        {
            var serializer = Serializer();
            writer.Write("{\"SchemaVersion\":1,\"SourceIP\":"); writer.Write(serializer.Serialize(sourceIP));
            writer.Write(",\"IdentityRule\":\"udp-payload\",\"NextNumber\":"); writer.Write(nextNumber.ToString(CultureInfo.InvariantCulture));
            writer.Write(",\"InterfaceWidthRule\":4");
            writer.Write(",\"NextCategoryNumbers\":");writer.Write(serializer.Serialize(categoryNumbers));
            writer.Write(",\"Commands\":[");
            for (int i = 0; i < entries.Length; i++) { writer.Write(i == 0 ? "\n" : ",\n"); writer.Write(serializer.Serialize(entries[i])); }
            writer.Write("\n]}\n");
        }
        private void SaveFile(bool checkExisting)
        {
            if (checkExisting && (!File.Exists(path) || new FileInfo(path).Length != savedLength || File.GetLastWriteTimeUtc(path) != savedTime))
                throw new InvalidOperationException("指令配置已被外部修改，请停止服务器并重启以载入修改；不会覆盖该文件");
            Directory.CreateDirectory(Path.GetDirectoryName(path)); string temporary = path + ".tmp";
            try
            {
                using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 65536))
                using (var writer = new StreamWriter(file, new UTF8Encoding(false), 65536, true))
                { WriteConfiguration(writer, configuration.Commands.ToArray(), configuration.SourceIP, configuration.NextNumber,configuration.NextCategoryNumbers); writer.Flush(); file.Flush(true); }
                if (File.Exists(path)) File.Replace(temporary, path, path + ".bak"); else File.Move(temporary, path);
                Stamp();
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        public string Observe(string source, string destination, byte[] payload, DateTime time)
        {
            lock (gate)
            {
                // Captured fragments remain visible and matchable, but never mutate the catalog.
                if (source != configuration.SourceIP || CommandEnvelope.HasMultiplePackets(payload)) return "";
                if (!ready) return error;
                try
                {
                    destination=IPv4(destination);string hex = Hex(payload);
                    var matches=MatchCommand(payload).Entries;
                    if(matches.Length>0)
                    {
                        var additions=matches.Where(existing=>!existing.DestinationIPs.Contains(destination)).ToArray();if(additions.Length==0)return error;
                        var previousCounters=new Dictionary<string,long>(configuration.NextCategoryNumbers);
                        var updates=new List<Tuple<ObservedCommand,ObservedCommand>>();
                        try
                        {
                            foreach(var existing in additions)
                            {
                                var updated=new ObservedCommand{Number=existing.Number,Name=existing.Name,DestinationIP=existing.DestinationIP,DestinationIPs=existing.DestinationIPs.Concat(new[]{destination}).ToList(),PayloadHex=existing.PayloadHex,Length=existing.Length,STC=existing.STC,InterfaceBytes=existing.InterfaceBytes,FormatIssue=existing.FormatIssue,FirstSeenUtc=existing.FirstSeenUtc,Aliases=existing.Aliases.ToList(),Categories=existing.Categories.Select(CloneCategory).ToList()};
                                EnsureCategories(configuration,updated);updates.Add(Tuple.Create(existing,updated));configuration.Commands[configuration.Commands.IndexOf(existing)]=updated;
                            }
                            SaveFile(true);
                        }
                        catch{foreach(var update in updates)configuration.Commands[configuration.Commands.IndexOf(update.Item2)]=update.Item1;configuration.NextCategoryNumbers=previousCounters;throw;}
                        foreach(var update in updates){var bucket=index[Key(PacketCodec.Hex(update.Item1.PayloadHex),update.Item1.InterfaceBytes)];bucket[bucket.IndexOf(update.Item1)]=update.Item2;}revision++;error="";return "";
                    }
                    if (configuration.NextNumber < 1 || configuration.NextNumber == Int64.MaxValue) throw new InvalidOperationException("指令编号已超出可用范围");
                    long number = configuration.NextNumber;
                    var entry = new ObservedCommand { Number = number, Name = "指令" + number.ToString(CultureInfo.InvariantCulture), DestinationIP = destination, DestinationIPs=new List<string>{destination}, PayloadHex = hex, Length = payload.Length, FirstSeenUtc = time.ToUniversalTime().ToString("o") };
                    var previousCategoryCounters=new Dictionary<string,long>(configuration.NextCategoryNumbers);EnsureCategories(configuration,entry);
                    configuration.Commands.Add(entry); configuration.NextNumber++;
                    try { SaveFile(true); }
                    catch { configuration.Commands.RemoveAt(configuration.Commands.Count - 1); configuration.NextNumber = number; configuration.NextCategoryNumbers=previousCategoryCounters; throw; }
                    AddIndex(entry, payload); revision++; error = "";
                }
                catch (Exception ex) { error = "指令配置保存失败：" + ex.Message; }
                return error;
            }
        }
        public object Info()
        {
            lock (gate) return new { ready = ready, sourceIP = configuration == null ? "192.168.1.16" : configuration.SourceIP,
                migratedInterfaceCommands=migratedInterfaceCommands,formatIssues=configuration.Commands.Where(c=>!String.IsNullOrEmpty(c.FormatIssue)).Select(c=>c.Name+": "+c.FormatIssue).ToArray(),count = ready ? configuration.Commands.Count : 0, nextNumber = ready ? configuration.NextNumber : 1, revision = revision, categoryCounts=configuration.Commands.SelectMany(c=>c.Categories).GroupBy(c=>c.Kind).ToDictionary(g=>g.Key,g=>g.Count()),
                file = "config/observed-commands.json", identity = "XML先按数据区包含完整BC数据区跳过，其余按完整UDP判重；接收先识别BC，命中仅采用BC及其绑定，未命中按功能ID、接口类型和完整数据区匹配；普通UDP按完整载荷匹配", error = error };
        }
        public void Export(TextWriter writer)
        {
            ObservedCommand[] entries; string source; long next;Dictionary<string,long> counters;
            lock (gate)
            {
                if (!ready) throw new InvalidOperationException(error);
                entries = configuration.Commands.ToArray(); source = configuration.SourceIP; next = configuration.NextNumber;counters=new Dictionary<string,long>(configuration.NextCategoryNumbers);
            }
            WriteConfiguration(writer, entries, source, next,counters);
        }
    }
}
