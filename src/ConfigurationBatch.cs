using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace TelemetryMonitor
{
    public sealed partial class Server
    {
        private string MergeConfigurationFile(ConfigurationEditor editor,string name,string current,string pending)
        {
            string baseline;editor.Base.TryGetValue(name,out baseline);
            if(SameConfigText(current,baseline))return pending;
            var serializer=ConfigurationJson();object value=ConfigurationMerge.Merge(baseline==null?null:serializer.DeserializeObject(baseline),current==null?null:serializer.DeserializeObject(current),pending==null?null:serializer.DeserializeObject(pending),name);
            return value==null?null:serializer.Serialize(value);
        }
        private static void WriteBatchFile(string directory,string name,string value)
        {string path=Path.Combine(directory,"config",name);if(value==null){if(File.Exists(path))File.Delete(path);}else File.WriteAllText(path,value,new UTF8Encoding(false));}
        private void ApplyConfigurationBatch(ConfigurationEditor editor,string[] changedFiles)
        {
            const string commandCatalog="observed-commands.json";
            string directory=Path.Combine(ConfigurationEditorRoot,"apply"),liveDirectory=Path.Combine(root,"config");
            var changed=new HashSet<string>(changedFiles);
            var before=new Dictionary<string,string>();
            var pendingValues=new Dictionary<string,string>();
            var writes=new Dictionary<string,byte[]>();
            var workingFiles=new HashSet<string>();
            Monitor incoming=null;
            SendMonitorSettings previousCapture=null;
            Settings previousSettings=null;
            bool reuse=true,owned=false,captureChanged=false,socketChanged=false;
            try
            {
                // The command catalog is the only file updated by packet capture.
                foreach(string name in ConfigurationFiles)
                {
                    if(name==commandCatalog)continue;
                    before[name]=ConfigFile(root,name);
                    string baseline;editor.Base.TryGetValue(name,out baseline);
                    if(!SameConfigText(before[name],baseline)){reuse=false;changed.Add(name);}
                }
                if(!reuse||changed.Contains(commandCatalog))
                    lock(liveMonitor.Outgoing.Commands.ConfigurationGate)before[commandCatalog]=ConfigFile(root,commandCatalog);
                foreach(string name in changed)
                    pendingValues[name]=MergeConfigurationFile(editor,name,before[name],ConfigFile(editor.Root,name));

                // Reuse the validated editor and avoid writing a second set of every
                // configuration file unless external changes require a reload.
                incoming=editor.Monitor;
                if(!reuse)
                {
                    Directory.CreateDirectory(Path.Combine(directory,"config"));
                    foreach(string name in ConfigurationFiles)
                    {
                        string value;workingFiles.Add(name);
                        WriteBatchFile(directory,name,pendingValues.TryGetValue(name,out value)?value:before[name]);
                    }
                    incoming=new Monitor(directory,true);
                    owned=true;
                    foreach(string name in ConfigurationFiles)pendingValues[name]=ConfigFile(directory,name);
                }
                bool catalogChanged=changed.Contains(commandCatalog)||(!reuse&&!SameConfigText(before[commandCatalog],pendingValues[commandCatalog]));
                ObservedCommands catalog=incoming.Outgoing.Commands;
                if(catalogChanged)
                {
                    if(reuse)
                    {
                        Directory.CreateDirectory(Path.Combine(directory,"config"));
                        workingFiles.Add(commandCatalog);
                        WriteBatchFile(directory,commandCatalog,pendingValues[commandCatalog]);
                    }
                    catalog=new ObservedCommands(directory);
                    pendingValues[commandCatalog]=ConfigFile(directory,commandCatalog);
                }
                if(catalogChanged&&catalog.Error.Length>0)throw new InvalidOperationException(catalog.Error);
                var transmitter=reuse?editor.Transmitter:new CommandTransmitter(directory,catalog,(level,message)=>incoming.Note(level,message));

                foreach(var item in pendingValues)
                {
                    if(item.Value==null||(item.Key==commandCatalog&&!catalogChanged)||SameConfigText(before[item.Key],item.Value))continue;
                    writes[item.Key]=Encoding.UTF8.GetBytes(item.Value);
                    changed.Add(item.Key);
                }
                string[] effective=changed.ToArray();
                var runtimeChange=liveMonitor.PrepareRuntimeConfiguration(incoming,effective);
                if(changed.Contains("server.json"))previousSettings=JsonFile.Decode<Settings>(JsonFile.Encode(liveMonitor.Config));
                liveMonitor.ApplyingConfiguration=true;
                if(changed.Contains("send-monitor.json"))
                {
                    previousCapture=liveMonitor.Outgoing.ConfigurationCopy();
                    liveMonitor.Outgoing.ApplyHotSettings(incoming.Outgoing.ConfigurationCopy());captureChanged=true;
                }
                lock(liveMonitor.Gate)lock(liveMonitor.Outgoing.ConfigurationGate)lock(liveMonitor.Outgoing.Commands.ConfigurationGate)
                {
                    foreach(string name in ConfigurationFiles)
                    {
                        if(name==commandCatalog)
                        {
                            if(!catalogChanged)continue;
                            string current=ConfigFile(root,name);
                            if(!SameConfigText(current,before[name]))
                            {
                                WriteBatchFile(directory,name,MergeConfigurationFile(editor,name,current,ConfigFile(editor.Root,name)));
                                catalog=new ObservedCommands(directory);
                                if(catalog.Error.Length>0)throw new InvalidOperationException(catalog.Error);
                                string value=ConfigFile(directory,name);
                                if(value==null||SameConfigText(current,value))writes.Remove(name);
                                else writes[name]=Encoding.UTF8.GetBytes(value);
                            }
                        }
                        else if(!SameConfigText(ConfigFile(root,name),before[name]))throw new InvalidOperationException(name+"在准备期间被外部修改，请重试；原数据已保留");
                    }
                    var plans=new List<CommandEditFile>();
                    foreach(string name in ConfigurationFiles)
                    {
                        byte[] bytes;if(writes.TryGetValue(name,out bytes))plans.Add(CommandEditFiles.Plan(liveDirectory,name,bytes));
                    }
                    liveMonitor.ArchiveChangedConfigurationData(runtimeChange);
                    if(changed.Contains("server.json")){liveMonitor.ApplyHotSocket(incoming.Config);socketChanged=true;}
                    if(plans.Count>0)CommandEditFiles.Commit(liveDirectory,plans);
                    if(catalogChanged)liveMonitor.Outgoing.Commands.AdoptConfiguration(catalog);
                    if(catalogChanged||changed.Contains("command-send.json"))liveTransmitter.AdoptConfiguration(transmitter);
                    liveMonitor.AdoptRuntimeConfiguration(incoming,runtimeChange,liveTransmitter.SequenceRevision,effective);
                }
            }
            catch
            {
                if(socketChanged)try{liveMonitor.ApplyHotSocket(previousSettings);}catch(Exception ex){liveMonitor.Note("error","接收网口恢复失败："+ex.Message);}
                if(captureChanged)try{liveMonitor.Outgoing.ApplyHotSettings(previousCapture);}catch(Exception ex){liveMonitor.Note("error","发送监视网口恢复失败："+ex.Message);}
                throw;
            }
            finally
            {
                liveMonitor.ApplyingConfiguration=false;
                if(owned&&incoming!=null)incoming.Dispose();
                try{DeleteConfigurationCopies(directory,workingFiles);}
                catch(IOException ex){liveMonitor.Note("warn","配置应用临时文件清理失败："+ex.Message,false);}
                catch(UnauthorizedAccessException ex){liveMonitor.Note("warn","配置应用临时文件清理失败："+ex.Message,false);}
            }
        }
    }
}
