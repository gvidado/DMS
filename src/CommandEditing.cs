using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace TelemetryMonitor
{
    // A small recovery journal makes edits spanning the library and its references reversible.
    internal sealed class CommandEditFile
    { public string Name,Before,After; }
    internal static class CommandEditFiles
    {
        private const string Journal="command-edit.pending.json";
        private static JavaScriptSerializer Serializer(){return new JavaScriptSerializer{MaxJsonLength=Int32.MaxValue};}
        private static string Read(string path){return File.Exists(path)?Convert.ToBase64String(File.ReadAllBytes(path)):null;}
        internal static CommandEditFile Plan(string directory,string name,byte[] bytes)
        {return new CommandEditFile{Name=name,Before=Read(Path.Combine(directory,name)),After=Convert.ToBase64String(bytes)};}
        private static void Write(string path,string value)
        {
            if(value==null){if(File.Exists(path))File.Delete(path);return;}
            string temporary=path+".edit.tmp";
            try
            {
                using(var file=new FileStream(temporary,FileMode.Create,FileAccess.Write,FileShare.None))
                {var bytes=Convert.FromBase64String(value);file.Write(bytes,0,bytes.Length);file.Flush(true);}
                if(File.Exists(path))File.Replace(temporary,path,path+".bak");else File.Move(temporary,path);
            }
            finally{if(File.Exists(temporary))File.Delete(temporary);}
        }
        internal static bool Pending(string directory){return File.Exists(Path.Combine(directory,Journal));}
        internal static void Recover(string directory)
        {
            string journal=Path.Combine(directory,Journal);if(!File.Exists(journal))return;
            var files=Serializer().Deserialize<List<CommandEditFile>>(File.ReadAllText(journal,Encoding.UTF8));
            var allowed=new[]{"observed-commands.json","continuous-rules.json","sequences.json","server.json","parameters.json","decoder.json","tc-bindings.json","send-monitor.json","command-send.json"};
            if(files==null || files.Count<1 || files.Count>allowed.Length || files.Any(f=>f==null || !allowed.Contains(f.Name)) || files.Select(f=>f.Name).Distinct().Count()!=files.Count)
                throw new InvalidOperationException("指令编辑恢复记录无效，请保留配置文件并检查");
            foreach(var file in files)
            {
                string current=Read(Path.Combine(directory,file.Name));
                if(current!=file.Before && current!=file.After)throw new InvalidOperationException("未完成的指令编辑涉及外部修改："+file.Name+"；已保留原文件，请检查后恢复");
            }
            foreach(var file in files)if(Read(Path.Combine(directory,file.Name))!=file.Before)Write(Path.Combine(directory,file.Name),file.Before);
            File.Delete(journal);
        }
        internal static void Commit(string directory,List<CommandEditFile> files)
        {
            if(Pending(directory))throw new InvalidOperationException("存在未完成的指令编辑，请重启服务器完成恢复");
            foreach(var file in files)if(Read(Path.Combine(directory,file.Name))!=file.Before)throw new InvalidOperationException(file.Name+"已变化，请刷新后重试");
            Write(Path.Combine(directory,Journal),Convert.ToBase64String(new UTF8Encoding(false).GetBytes(Serializer().Serialize(files))));
            try
            {
                foreach(var file in files)
                {
                    if(Read(Path.Combine(directory,file.Name))!=file.Before)throw new InvalidOperationException(file.Name+"已被外部修改，停止保存");
                    Write(Path.Combine(directory,file.Name),file.After);
                }
                File.Delete(Path.Combine(directory,Journal));
            }
            catch{Recover(directory);throw;}
        }
    }

    public sealed partial class ObservedCommands
    {
        internal CommandImportPlan PreviewEdit(long number,string originalCode,string code,string name,long expectedRevision,string destinationIPs="")
        {
            lock(gate)
            {
                if(!ready)throw new InvalidOperationException(error);
                if(revision!=expectedRevision)throw new InvalidOperationException("指令库已变化，请关闭编辑窗口并刷新后重试");
                name=(name??"").Trim();code=(code??"").Trim().ToUpperInvariant();originalCode=originalCode??"";
                if(name.Length==0 || name.Length>256 || name.IndexOf('\0')>=0)throw new ArgumentException("指令名称不能为空，最多256个字符");
                var copy=Serializer().Deserialize<ObservedCommandConfiguration>(Serializer().Serialize(configuration));
                var entry=copy.Commands.FirstOrDefault(e=>e.Number==number);if(entry==null)throw new ArgumentException("指令已不存在，请刷新");
                if(entry.STC && !String.IsNullOrWhiteSpace(destinationIPs))
                {
                    var destinations=destinationIPs.Split(new[]{';','；',',','，',' ','\r','\n','\t'},StringSplitOptions.RemoveEmptyEntries).Select(IPv4).Distinct().ToList();
                    if(destinations.Count<1 || destinations.Count>64)throw new ArgumentException("STC指令应配置1～64个目的IP");
                    entry.DestinationIPs=destinations;entry.DestinationIP=destinations[0];
                }
                if(originalCode.Length==0)
                {if(code.Length>0)throw new ArgumentException("未分类指令只能修改名称，编号需先按目的IP归类");}
                else
                {
                    var category=entry.Categories.FirstOrDefault(c=>c.Code==originalCode);if(category==null)throw new InvalidOperationException("指令编号已变化，请刷新");
                    if(entry.STC)
                    {
                        string kind=new[]{"ZK","BC","KC","PSS"}.FirstOrDefault(k=>code.StartsWith(k,StringComparison.Ordinal));
                        if(kind==null)throw new ArgumentException("STC指令编号前缀应为ZK、BC、KC或PSS");
                        if(entry.Categories.Any(c=>c!=category&&c.Kind==kind))throw new ArgumentException("该指令已有"+kind+"类别");category.Kind=kind;
                    }
                    CodeNumber(category.Kind,code);
                    if(copy.Commands.SelectMany(e=>e.Categories).Any(c=>c!=category && c.Code==code))throw new ArgumentException("指令编号已存在："+code);
                    category.Code=code;
                }
                entry.Name=name;NormalizeCategories(copy);
                return new CommandImportPlan{Revision=revision,Configuration=copy};
            }
        }
        internal void CommitEdit(CommandImportPlan plan,List<CommandEditFile> related)
        {
            lock(gate)
            {
                if(!ready || revision!=plan.Revision)throw new InvalidOperationException("指令库已变化，请刷新后重试");
                if(!File.Exists(path) || new FileInfo(path).Length!=savedLength || File.GetLastWriteTimeUtc(path)!=savedTime)
                    throw new InvalidOperationException("指令配置已被外部修改，请重启以载入修改；不会覆盖该文件");
                using(var writer=new StringWriter(System.Globalization.CultureInfo.InvariantCulture))
                {
                    var copy=plan.Configuration;WriteConfiguration(writer,copy.Commands.ToArray(),copy.SourceIP,copy.NextNumber,copy.NextCategoryNumbers);
                    related.Add(CommandEditFiles.Plan(Path.GetDirectoryName(path),"observed-commands.json",new UTF8Encoding(false).GetBytes(writer.ToString())));
                }
                try{CommandEditFiles.Commit(Path.GetDirectoryName(path),related);}
                catch
                {
                    if(CommandEditFiles.Pending(Path.GetDirectoryName(path))){ready=false;error="指令编辑保存未完成，请重启服务器检查恢复；原文件已保留";}
                    else Stamp();
                    throw;
                }
                configuration=plan.Configuration;Stamp();index.Clear();
                foreach(var entry in configuration.Commands)AddIndex(entry,entry.Length==0?new byte[0]:PacketCodec.Hex(entry.PayloadHex));
                revision++;error="";
            }
        }
    }

    public sealed partial class Monitor
    {
        private void MigratePssCommandNumbers()
        {
            // Use the existing journaled edit so saved references move with each code.
            ObservedCommand[] entries;
            try{entries=Outgoing.Commands.BindingEntries();}catch{return;}
            int migrated=0;
            foreach(var entry in entries)foreach(var category in entry.Categories.Where(c=>c.Kind=="PSS"))
            {
                long number;if(!Int64.TryParse(category.Code.Substring(3),out number))continue;
                string canonical=ObservedCommands.CategoryCode("PSS",number);if(canonical==category.Code)continue;
                try
                {
                    EditCommand(entry.Number,category.Code,canonical,entry.Name,Outgoing.Commands.BindingRevision);migrated++;
                }
                catch(Exception ex){Note("warn","PSS编号转换未完成，原编号和引用保留："+category.Code+" → "+canonical+"；"+ex.Message);}
            }
            if(migrated>0)Note("info","已将"+migrated+"条PSS指令改为三位数字编号，并同步已保存规则与序列引用。");
        }
        // The language has identifiers and // comments, but no quoted string literals.
        internal static string RenameCommandReference(string script,string oldCode,string newCode)
        {
            return Regex.Replace(script??"",@"//[^\r\n]*|[\p{L}_][\p{L}\p{Nd}_.]*",m=>m.Value=="TC."+oldCode?"TC."+newCode:m.Value);
        }
        private static CommandEditFile PlanCommandReferenceFile<T>(string directory,string filename,T before,T after)
        {
            string path=Path.Combine(directory,filename);
            if(!File.Exists(path) || !CanonicalJson.Equivalent(JsonFile.Load<T>(path),before))
                throw new InvalidOperationException(filename+"已被外部修改，请重启以载入修改；不会覆盖该文件");
            return CommandEditFiles.Plan(directory,filename,new UTF8Encoding(false).GetBytes(JsonFile.Encode(after)));
        }
        internal object EditCommand(long number,string originalCode,string code,string name,long revision,string destinationIPs="")
        {
            lock(Gate)
            {
                if(Listening || Demo || Outgoing.IsActive || judgmentRunning || AnySequenceRunning)throw new InvalidOperationException("请先停止接收、常态化判读和序列，再修改指令");
                string directory=Path.Combine(root,"config");
                if(CommandEditFiles.Pending(directory))throw new InvalidOperationException("存在未完成的指令编辑，请重启服务器完成恢复");
                if(tcStorageError.Length>0 || ruleStorageError.Length>0 || sequenceStorageError.Length>0)throw new InvalidOperationException("请先修复TC绑定、判读规则或序列配置的读取错误，再编辑指令");
                code=(code??"").Trim().ToUpperInvariant();var plan=Outgoing.Commands.PreviewEdit(number,originalCode,code,name,revision,destinationIPs);
                string issue=CommandImportError(plan);if(issue.Length>0)throw new ArgumentException(issue);
                code=(code??"").Trim();bool changed=originalCode.Length>0 && code!=originalCode;
                var rules=SequenceClone(continuous);var sequencesCopy=SequenceClone(sequenceConfiguration);
                int ruleCount=0,sequenceRows=0;var files=new List<CommandEditFile>();
                if(changed)
                {
                    foreach(var rule in rules.Rules)
                    {
                        string script=RenameCommandReference(rule.Script,originalCode,code);if(script==rule.Script)continue;
                        rule.Script=script;rule.ModifiedAt=BeijingTime.Now.ToString("o");ValidateRuleFields(rule);RuleParser.Compile(script);ruleCount++;
                    }
                    foreach(var sequence in sequencesCopy.Sequences)foreach(var row in sequence.Rows)
                    {
                        string value=(row.Command??"").Trim();
                        if(value==originalCode){row.Command=code;sequenceRows++;}
                        else if(value=="TC."+originalCode){row.Command="TC."+code;sequenceRows++;}
                    }
                }
                if(ruleCount>0){rules.Revision=Guid.NewGuid().ToString("N");files.Add(PlanCommandReferenceFile(directory,"continuous-rules.json",continuous,rules));}
                if(sequenceRows>0){sequencesCopy.Revision=Guid.NewGuid().ToString("N");files.Add(PlanCommandReferenceFile(directory,"sequences.json",sequenceConfiguration,sequencesCopy));}
                Outgoing.Commands.CommitEdit(plan,files);
                if(ruleCount>0)continuous=rules;if(sequenceRows>0)sequenceConfiguration=sequencesCopy;
                ruleBindings.Clear();ruleRuns.Clear();
                return new{ok=true,renamedRules=ruleCount,renamedSequenceRows=sequenceRows};
            }
        }
    }
}
