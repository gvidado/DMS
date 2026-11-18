using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace TelemetryMonitor
{
    internal sealed class ConfigurationEditor
    {
        internal Server Owner;internal string Root;internal Monitor Monitor;internal CommandTransmitter Transmitter;
        internal Dictionary<string,string> Base=new Dictionary<string,string>(),Initial=new Dictionary<string,string>();
    }
    internal sealed class PendingConfigurationManifest
    {
        public Dictionary<string,string> Base=new Dictionary<string,string>(),Initial=new Dictionary<string,string>();
        public string SavedAt="",Actor="";
    }
    public sealed partial class Server
    {
        [ThreadStatic]private static ConfigurationEditor configurationContext;
        [ThreadStatic]private static ConfigurationRequestScope configurationRequest;
        private ConfigurationEditor configurationEditor;
        private Timer configurationTimer;
        private readonly object configurationEditGate=new object();
        private int configurationScheduleBusy;
        private readonly Stopwatch configurationClock=Stopwatch.StartNew();
        private const int ConfigurationIntervalSeconds=120;
        private double configurationNextTick=ConfigurationIntervalSeconds;
        private string configurationError="",configurationLastApplied="",configurationLastApplyId="",configurationSavedAt="",configurationActor="";
        private string[] configurationLastFiles=new string[0];private double configurationLastDurationMs;
        private readonly HashSet<string> configurationDirty=new HashSet<string>();
        private static readonly string[] ConfigurationFiles={"server.json","parameters.json","decoder.json","continuous-rules.json","tc-bindings.json","sequences.json","observed-commands.json","send-monitor.json","command-send.json"};
        private string ConfigurationEditorRoot {get{return Path.Combine(root,"data","pending-configuration");}}
        private static JavaScriptSerializer ConfigurationJson(){return new JavaScriptSerializer{MaxJsonLength=Int32.MaxValue,RecursionLimit=128};}
        private static string ConfigFile(string directory,string name){string path=Path.Combine(directory,"config",name);return File.Exists(path)?File.ReadAllText(path,Encoding.UTF8).TrimStart('\uFEFF'):null;}
        private static bool SameConfigText(string a,string b)
        {
            if (String.Equals(a,b,StringComparison.Ordinal)) return true;
            if (a==null || b==null) return false;
            var serializer = ConfigurationJson();
            return CanonicalJson.EqualParsed(serializer.DeserializeObject(a),serializer.DeserializeObject(b));
        }
        private static bool ConfigurationCommit(string path)
        {
            return path.EndsWith("-apply",StringComparison.Ordinal)||new[]{"/api/config","/api/decoders/apply","/api/decoders/packet","/api/decoders/packet/delete","/api/decoders/parameter","/api/continuous-rules/apply","/api/continuous-rules/save","/api/continuous-rules/delete","/api/continuous-rules/toggle","/api/continuous-rules/tc","/api/sequence/apply","/api/sequence/save-edits","/api/sequence/config","/api/send-monitor/config","/api/send-monitor/commands/apply","/api/send-monitor/commands/clear","/api/send-monitor/commands/edit","/api/command-send/config"}.Contains(path);
        }
        private static bool ConfigurationRoute(string path)
        {
            return path=="/api/config"||path.StartsWith("/api/decoders",StringComparison.Ordinal)||
                (path.StartsWith("/api/continuous-rules",StringComparison.Ordinal)&&!new[]{"/api/continuous-rules/control","/api/continuous-rules/globals"}.Contains(path)&&!path.StartsWith("/api/continuous-rules/abnormals",StringComparison.Ordinal))||
                (path.StartsWith("/api/sequence/",StringComparison.Ordinal)&&new[]{"config","definition","preview","preview/cancel","recheck","apply","edit-preview","save-edits","export-converted"}.Any(t=>path=="/api/sequence/"+t))||
                path=="/api/send-monitor/config"||path=="/api/send-monitor/state"||path.StartsWith("/api/send-monitor/commands/",StringComparison.Ordinal)||path=="/api/command-send/config";
        }
        private void InitializeConfigurationSchedule()
        {
            lock(configurationEditGate)lock(commandGate)
            {
                string manifest=Path.Combine(ConfigurationEditorRoot,"pending.json");
                if(File.Exists(manifest))try
                {
                    var saved=ConfigurationJson().Deserialize<PendingConfigurationManifest>(File.ReadAllText(manifest,Encoding.UTF8));
                    configurationEditor=CreateConfigurationEditor(ConfigurationEditorRoot,saved.Base,saved.Initial);configurationSavedAt=saved.SavedAt;configurationActor=saved.Actor;RefreshConfigurationDirty();
                }
                catch(Exception ex){configurationError="待生效配置恢复失败，原文件保留："+ex.Message;}
                if(configurationEditor!=null&&configurationDirty.Count==0)RetireConfigurationEditor();
                ApplyIdleConfiguration();
            }
            configurationTimer=new Timer(ConfigurationScheduleTick,null,1000,1000);
        }
        private ConfigurationEditor CreateConfigurationEditor(string directory,Dictionary<string,string> baseline,Dictionary<string,string> initial)
        {
            var editor=new ConfigurationEditor{Owner=this,Root=directory,Base=baseline,Initial=initial};editor.Monitor=new Monitor(directory,true);
            editor.Transmitter=new CommandTransmitter(directory,editor.Monitor.Outgoing.Commands,(level,message)=>editor.Monitor.Note(level,message));editor.Transmitter.UseEditorRevision(liveTransmitter.SequenceRevision);return editor;
        }
        private ConfigurationEditor GetConfigurationEditor()
        {
            if(configurationEditor!=null)return configurationEditor;
            if(configurationError.Length>0&&File.Exists(Path.Combine(ConfigurationEditorRoot,"pending.json")))throw new InvalidOperationException(configurationError);
            string directory=ConfigurationEditorRoot;Directory.CreateDirectory(Path.Combine(directory,"config"));var baseline=new Dictionary<string,string>();
            foreach(string name in ConfigurationFiles)
            {
                string value;
                if(name=="observed-commands.json")
                    lock(liveMonitor.Outgoing.Commands.ConfigurationGate)value=ConfigFile(root,name);
                else value=ConfigFile(root,name);
                baseline[name]=value;
                WriteBatchFile(directory,name,value);
            }
            configurationEditor=CreateConfigurationEditor(directory,baseline,new Dictionary<string,string>());
            foreach(string name in ConfigurationFiles)
            {
                string text=ConfigFile(directory,name);
                configurationEditor.Initial[name]=String.Equals(text,baseline[name],StringComparison.Ordinal)?baseline[name]:text;
            }
            return configurationEditor;
        }
        private static IEnumerable<string> CommitConfigurationFiles(string path)
        {
            if(path=="/api/config")return new[]{"server.json"};
            if(path.StartsWith("/api/decoders/",StringComparison.Ordinal))return new[]{"decoder.json","continuous-rules.json"};
            if(path=="/api/continuous-rules/tc")return new[]{"tc-bindings.json"};
            if(path.StartsWith("/api/continuous-rules/",StringComparison.Ordinal))return new[]{"continuous-rules.json"};
            if(path.StartsWith("/api/sequence/",StringComparison.Ordinal))return new[]{"sequences.json"};
            if(path=="/api/send-monitor/config")return new[]{"send-monitor.json"};
            if(path=="/api/command-send/config")return new[]{"command-send.json"};
            if(path.StartsWith("/api/send-monitor/commands/",StringComparison.Ordinal))
                return new[]{"observed-commands.json","tc-bindings.json","sequences.json","continuous-rules.json"};
            return ConfigurationFiles;
        }

        private void RefreshConfigurationDirty(IEnumerable<string> files=null)
        {
            if(files==null)configurationDirty.Clear();
            if(configurationEditor==null)return;
            foreach(string name in files??ConfigurationFiles)
            {
                string previous;
                configurationEditor.Initial.TryGetValue(name,out previous);
                if(!SameConfigText(previous,ConfigFile(configurationEditor.Root,name)))configurationDirty.Add(name);
                else configurationDirty.Remove(name);
            }
        }
        private void PersistConfigurationEditor()
        {
            if(configurationEditor==null)return;
            string path=Path.Combine(ConfigurationEditorRoot,"pending.json");
            if(configurationDirty.Count==0)
            {
                if(File.Exists(path))File.Delete(path);
                return;
            }
            var value=new PendingConfigurationManifest{Base=configurationEditor.Base,Initial=configurationEditor.Initial,SavedAt=configurationSavedAt,Actor=configurationActor};
            string temp=path+".tmp";File.WriteAllText(temp,ConfigurationJson().Serialize(value),new UTF8Encoding(false));
            if(File.Exists(path))File.Replace(temp,path,null);else File.Move(temp,path);
        }
        private sealed class ConfigurationRequestScope:IDisposable
        {
            private readonly Server owner;private readonly HttpListenerContext context;private readonly bool commit;private bool completed;
            internal ConfigurationRequestScope(Server server,HttpListenerContext ctx)
            {
                owner=server;context=ctx;commit=ctx.Request.HttpMethod=="POST"&&ConfigurationCommit(ctx.Request.Url.AbsolutePath);
                System.Threading.Monitor.Enter(owner.configurationEditGate);
                try
                {
                    configurationContext=owner.GetConfigurationEditor();
                    configurationRequest=this;
                    if(commit)
                    {
                        var cookie=ctx.Request.Cookies["tm_session"];
                        var session=cookie==null?null:owner.auth.Find(cookie.Value);
                        if(session!=null)owner.configurationActor=owner.auth.User(session).DisplayName;
                    }
                }
                catch{System.Threading.Monitor.Exit(owner.configurationEditGate);throw;}
            }
            internal object BeforeResponse(HttpListenerResponse response,object value,int status)
            {
                if(!commit||completed||status>=400||!Object.ReferenceEquals(response,context.Response))return value;
                completed=true;
                lock(owner.commandGate)
                {
                    owner.RefreshConfigurationDirty(CommitConfigurationFiles(context.Request.Url.AbsolutePath));owner.configurationSavedAt=BeijingTime.Now.ToString("yyyy-MM-dd HH:mm:ss");owner.configurationError="";
                    try{owner.PersistConfigurationEditor();}catch(Exception ex){owner.configurationError="待生效配置持久化失败："+ex.Message;throw new IOException(owner.configurationError,ex);}
                    bool apply=owner.configurationDirty.Count>0&&!owner.liveMonitor.ConfigurationMonitoringActive;
                    if(apply)
                    {if(!owner.TryApplyConfiguration())throw new InvalidOperationException(owner.configurationError);configurationContext=null;response.Headers["X-Configuration-Applied"]="true";if(context.Request.Url.AbsolutePath=="/api/command-send/config")value=owner.liveTransmitter.Configuration();}
                    if(!owner.liveMonitor.ConfigurationMonitoringActive&&context.Request.Url.AbsolutePath=="/api/command-send/config")
                    {if(owner.configurationEditor!=null)owner.configurationEditor.Transmitter.UseEditorRevision(owner.liveTransmitter.SequenceRevision);value=owner.liveTransmitter.Configuration();}
                    response.Headers["X-Configuration-Pending"]=owner.configurationDirty.Count>0?"true":"false";
                }
                return value;
            }
            public void Dispose(){configurationContext=null;configurationRequest=null;System.Threading.Monitor.Exit(owner.configurationEditGate);}
        }
        private ConfigurationRequestScope EnterConfigurationRequest(HttpListenerContext ctx)
        {
            if(!ConfigurationRoute(ctx.Request.Url.AbsolutePath))return null;
            if(ctx.Request.HttpMethod=="GET"&&Query(ctx.Request,"draft")!="1")return null;
            return new ConfigurationRequestScope(this,ctx);
        }
        private object ConfigurationScheduleInfo()
        {
            lock(commandGate){bool active=liveMonitor.ConfigurationMonitoringActive;return new{intervalSeconds=ConfigurationIntervalSeconds,monitoring=active,pending=configurationDirty.Count>0,files=configurationDirty.OrderBy(x=>x).ToArray(),savedAt=configurationSavedAt,actor=configurationActor,
                nextInSeconds=active?Math.Max(0,Math.Ceiling(configurationNextTick-configurationClock.Elapsed.TotalSeconds)):0,lastAppliedAt=configurationLastApplied,applyId=configurationLastApplyId,lastAppliedFiles=configurationLastFiles,lastApplyMilliseconds=configurationLastDurationMs,error=configurationError};}
        }
        private bool HandleConfigurationSchedule(HttpListenerContext ctx,Session session)
        {
            if(ctx.Request.Url.AbsolutePath=="/api/configuration/schedule"&&ctx.Request.HttpMethod=="GET"){Json(ctx.Response,ConfigurationScheduleInfo(),200);return true;}
            if(ctx.Request.Url.AbsolutePath=="/api/configuration/cancel"&&ctx.Request.HttpMethod=="POST")
            {
                lock(configurationEditGate)lock(commandGate)
                {
                    DeleteConfigurationManifest(CheckedConfigurationEditorRoot());
                    RetireConfigurationEditor();
                    configurationError="";
                }
                Json(ctx.Response,ConfigurationScheduleInfo(),200);return true;
            }
            return false;
        }
        private void DisposeConfigurationEditor(){if(configurationEditor!=null){configurationEditor.Monitor.Dispose();configurationEditor=null;}}
        private string CheckedConfigurationEditorRoot()
        {
            string directory=Path.GetFullPath(ConfigurationEditorRoot),allowed=Path.GetFullPath(Path.Combine(root,"data"))+Path.DirectorySeparatorChar;
            if(!directory.StartsWith(allowed,StringComparison.OrdinalIgnoreCase))throw new IOException("待生效配置目录无效");
            return directory;
        }
        private static void DeleteConfigurationCopies(string directory,IEnumerable<string> names)
        {
            // Remove only known staging files; never traverse runtime data or user directories.
            foreach(string name in names)foreach(string suffix in new[]{"",".bak",".edit.tmp"})
            {
                string file=Path.Combine(directory,"config",name+suffix);
                if(File.Exists(file))File.Delete(file);
            }
        }
        private static void DeleteConfigurationManifest(string directory)
        {
            string path=Path.Combine(directory,"pending.json");
            if(File.Exists(path))File.Delete(path);
        }
        private void RetireConfigurationEditor()
        {
            configurationDirty.Clear();
            configurationSavedAt="";
            DisposeConfigurationEditor();
            try
            {
                string directory=CheckedConfigurationEditorRoot();
                DeleteConfigurationManifest(directory);
                DeleteConfigurationCopies(directory,ConfigurationFiles);
            }
            catch(IOException ex){liveMonitor.Note("warn","配置草稿清理失败："+ex.Message,false);}
            catch(UnauthorizedAccessException ex){liveMonitor.Note("warn","配置草稿清理失败："+ex.Message,false);}
        }
        private void ConfigurationScheduleTick(object ignored)
        {
            if(!running)return;
            if(Interlocked.Exchange(ref configurationScheduleBusy,1)!=0)return;
            try{lock(configurationEditGate)lock(commandGate)
            {
                if(!running)return;bool due=configurationClock.Elapsed.TotalSeconds>=configurationNextTick;
                if(due)do{configurationNextTick+=ConfigurationIntervalSeconds;}while(configurationNextTick<=configurationClock.Elapsed.TotalSeconds);
                bool active=liveMonitor.ConfigurationMonitoringActive;
                if(configurationDirty.Count==0){if(due&&configurationEditor!=null)RetireConfigurationEditor();return;}
                if(active&&!due)return;
                if(!active&&!due&&configurationError.Length>0)return;
                TryApplyConfiguration();
            }}
            catch(Exception ex)
            {
                lock(commandGate)
                {
                    string error="配置定时检查失败："+ex.Message;
                    if(configurationError!=error)liveMonitor.Note("error",error);
                    configurationError=error;
                }
            }
            finally{Interlocked.Exchange(ref configurationScheduleBusy,0);}
        }
        private bool TryApplyConfiguration()
        {
            var elapsed=Stopwatch.StartNew();var files=configurationDirty.ToArray();
            try{ApplyConfigurationBatch(configurationEditor,files);}
            catch(Exception ex){configurationError="本轮配置未生效，继续保留当前接收与记录："+ex.Message;liveMonitor.Note("error",configurationError);return false;}
            RetireConfigurationEditor();
            configurationLastFiles=files;configurationLastDurationMs=Math.Round(elapsed.Elapsed.TotalMilliseconds,1);configurationLastApplied=BeijingTime.Now.ToString("yyyy-MM-dd HH:mm:ss");configurationLastApplyId=Guid.NewGuid().ToString("N");configurationError="";return true;
        }
        // Caller holds configurationEditGate then commandGate, as do saves and the timer.
        private bool ApplyIdleConfiguration()
        {
            if(liveMonitor.ConfigurationMonitoringActive)return true;
            return configurationDirty.Count==0||TryApplyConfiguration();
        }
    }
}
