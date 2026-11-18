using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace TelemetryMonitor
{
    internal sealed class RuntimeConfigurationChange
    {
        internal bool DecoderChanged,CatalogChanged,RulesChanged,SequenceRuntimeChanged;
        internal readonly Dictionary<string,Parameter> ChangedParameters=new Dictionary<string,Parameter>();
        internal readonly HashSet<string> ChangedPacketRoutes=new HashSet<string>();
        internal readonly HashSet<string> RetainedRuleStates=new HashSet<string>();
    }

    public sealed partial class Monitor
    {
        private readonly Dictionary<string,DateTime> configurationSourceSince=new Dictionary<string,DateTime>();
        private readonly Dictionary<string,DateTime> configurationPacketSince=new Dictionary<string,DateTime>();
        private static T HotCopy<T>(T value){return JsonFile.Decode<T>(JsonFile.Encode(value));}
        private static string DecodeSettingsKey(Settings value){return CanonicalJson.Encode(new{value.ByteOrder,value.BitOrder,value.CrossByteOrder,value.PayloadOffset,value.Profile,value.MappingConfirmed,value.SourceIPs});}
        private static string PacketRouteKey(PacketDefinition p)
        {return CanonicalJson.Encode(new{p.OuterSourceId,p.SourceIP,p.Apid,p.Version,p.PacketType,p.SecondaryHeader,p.SourceAddress,p.ServiceType,p.ServiceSubtype,p.Enabled});}

        // Configuration writers are serialized by the server. Published definitions can
        // be compared without keeping reception and live judgment behind Monitor.Gate.
        internal RuntimeConfigurationChange PrepareRuntimeConfiguration(Monitor incoming,string[] changedFiles)
        {
            var change=new RuntimeConfigurationChange();
            change.CatalogChanged=changedFiles.Contains("decoder.json")||changedFiles.Contains("parameters.json");
            bool settingsChanged=changedFiles.Contains("server.json")&&DecodeSettingsKey(Config)!=DecodeSettingsKey(incoming.Config);
            change.DecoderChanged=change.CatalogChanged||settingsChanged;
            change.RulesChanged=changedFiles.Contains("continuous-rules.json");
            // A saved group only changes how sequence definitions are listed.
            change.SequenceRuntimeChanged=changedFiles.Contains("sequences.json") &&
                (sequenceStorageError!=incoming.sequenceStorageError || !CanonicalJson.Equivalent(
                    SequenceRuntimeSettings(sequenceConfiguration),SequenceRuntimeSettings(incoming.sequenceConfiguration)));
            if(settingsChanged)
            {
                foreach(var field in Catalog)change.ChangedParameters.Add(field.Id,field);
            }
            else if(change.CatalogChanged)
            {
                var previousPackets=decoder==null?new Dictionary<string,PacketDefinition>():decoder.Packets.ToDictionary(p=>p.Id);
                var nextPackets=incoming.decoder==null?new Dictionary<string,PacketDefinition>():incoming.decoder.Packets.ToDictionary(p=>p.Id);
                var changedPackets=new HashSet<string>(previousPackets.Where(item=>
                {
                    PacketDefinition next;
                    return !nextPackets.TryGetValue(item.Key,out next)||!CanonicalJson.Equivalent(item.Value,next);
                }).Select(item=>item.Key));
                var nextFields=incoming.Catalog.ToDictionary(p=>p.Id);
                foreach(var field in Catalog)
                {
                    Parameter next;
                    if(!nextFields.TryGetValue(field.Id,out next)||changedPackets.Contains(field.PacketId??"")||
                        !CanonicalJson.Equivalent(field,next))
                        change.ChangedParameters.Add(field.Id,field);
                }
            }
            if(change.CatalogChanged&&incoming.decoder!=null)
            {
                var previousRoutes=decoder==null?new Dictionary<string,string>():decoder.Packets.ToDictionary(p=>p.Id,PacketRouteKey);
                foreach(var packet in incoming.decoder.Packets)
                {
                    string previous;
                    if(!previousRoutes.TryGetValue(packet.Id,out previous)||previous!=PacketRouteKey(packet))
                        change.ChangedPacketRoutes.Add(packet.Id);
                }
            }
            if(change.RulesChanged)
            {
                var previousRules=continuous.Rules.ToDictionary(r=>r.Id);
                foreach(var next in incoming.continuous.Rules)
                {
                    ContinuousRule previous;
                    if(previousRules.TryGetValue(next.Id,out previous)&&SameContinuousDefinition(previous,next))
                        change.RetainedRuleStates.Add(next.Id);
                }
            }
            return change;
        }
        internal void ArchiveChangedConfigurationData(RuntimeConfigurationChange change)
        {
            if(change.ChangedParameters.Count==0)return;
            var entries=new List<object>();
            foreach(var source in engineeringHistory)foreach(var row in source.Value)
            {
                Parameter field;
                if(!change.ChangedParameters.TryGetValue(row.Key,out field))continue;
                var history=row.Value;
                entries.Add(new{parameterId=row.Key,code=field.Reference,name=field.Name,unit=field.Unit,sourceIP=source.Key,latest=history.Latest,samples=history.Samples.ToArray()});
            }
            if(entries.Count>0)
            {
                string directory=Path.Combine(root,"data","configuration-data");Directory.CreateDirectory(directory);
                string line=CanonicalJson.Encode(new{time=BeijingTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"),reason="配置切换前的参数数据，工程值和原码按原接收记录保留",rows=entries});
                using(var file=new FileStream(Path.Combine(directory,BeijingTime.Now.ToString("yyyy-MM-dd")+".jsonl"),FileMode.Append,FileAccess.Write,FileShare.Read))
                {using(var writer=new StreamWriter(file,new UTF8Encoding(false),65536,true)){writer.WriteLine(line);writer.Flush();}file.Flush(true);}
            }
        }
        internal void AdoptRuntimeConfiguration(Monitor incoming,RuntimeConfigurationChange change,string sendRevision,string[] changedFiles)
        {
            lock(Gate)
            {
                // Transfer validated models from the retired editor; its disposal does not clear configuration models.
                if(changedFiles.Contains("server.json"))Config=incoming.Config;
                if(change.CatalogChanged){decoder=incoming.decoder;Catalog=incoming.Catalog;}
                if(change.DecoderChanged)legacyRevision=Guid.NewGuid().ToString("N");
                foreach(string id in change.ChangedParameters.Keys){Values.Remove(id);foreach(var source in engineeringHistory.Values)source.Remove(id);}
                if(change.RulesChanged)
                {
                    continuous=incoming.continuous;ruleStorageError=incoming.ruleStorageError;
                    foreach(var key in ruleRuns.Keys.Where(key=>!change.RetainedRuleStates.Contains(key)).ToArray())ruleRuns.Remove(key);
                }
                if(changedFiles.Contains("tc-bindings.json")){tcBindings=incoming.tcBindings;commandBindingRevision=tcBindings.Revision;tcStorageError=incoming.tcStorageError;}
                if(changedFiles.Contains("sequences.json")){sequenceConfiguration=incoming.sequenceConfiguration;sequenceStorageError=incoming.sequenceStorageError;}
                if(change.RulesChanged||change.DecoderChanged||changedFiles.Contains("tc-bindings.json")||changedFiles.Contains("observed-commands.json")){ruleBindings.Clear();Interlocked.Increment(ref judgmentGeneration);}
                DateTime now=DateTime.UtcNow;
                foreach(string ip in Config.SourceIPs)if(!sources.ContainsKey(ip)){sources[ip]=new SourceState{IP=ip};configurationSourceSince[ip]=now;}
                foreach(string id in change.ChangedPacketRoutes)
                {
                    configurationPacketSince[id]=now;
                    foreach(string key in sequences.Keys.Where(k=>k.EndsWith(":"+id,StringComparison.Ordinal)).ToArray())sequences.Remove(key);
                }
                bool rebind=change.DecoderChanged||change.SequenceRuntimeChanged||changedFiles.Any(f=>new[]{"tc-bindings.json","observed-commands.json","command-send.json","send-monitor.json"}.Contains(f));
                if(rebind)foreach(var context in sequenceTasks.Values.Where(c=>c.Running).ToArray())InSequence(context,()=>
                {
                    try{RebindRunningSequence(sendRevision);}
                    catch(Exception ex)
                    {
                        // A task or its archive failing must not reverse an already committed batch.
                        StopSequenceCore("配置已切换，本任务接续失败："+ex.Message);
                        Note("error","配置已生效；序列任务接续失败，已保留已有结果："+ex.Message);
                    }
                });
                RefreshSequenceTasks();generation++;
                Note("info","待生效配置已统一切换；保留接收计数、原包、异常、判读和存档，运行中的任务使用新配置");
            }
        }
        private void RebindRunningSequence(string sendRevision)
        {
            var updated=sequenceConfiguration.Sequences.FirstOrDefault(s=>s.Id==sequenceDefinition.Id)??sequenceConfiguration.Sequences.FirstOrDefault(s=>s.Code==sequenceDefinition.Code);
            if(updated==null||updated.Mode!=sequenceDefinition.Mode)
            {StopSequenceCore(updated==null?"新配置已删除本序列，本次运行结束，原结果保留":"新配置改变了运行模式，本次运行结束，请按新模式重新启动");return;}
            var definition=HotCopy(updated);definition.Name=sequenceDefinition.Name;
            List<string> errors,warnings;var steps=CompileSequence(definition,sequenceConfiguration.DefaultTimeoutSeconds,out errors,out warnings);
            if(errors.Count>0)
            {RecordAlarm("error","配置切换异常",definition.Name+"："+String.Join("；",errors.Take(5)));StopSequenceCore("新配置无法继续本任务："+String.Join("；",errors.Take(5)));return;}
            var previous=sequenceRows.ToDictionary(r=>r.Step.Row.Row);var rows=new List<SequenceStepRun>();
            foreach(var step in steps)
            {
                SequenceStepRun old;
                if(previous.TryGetValue(step.Row.Row,out old)&&old.Terminal){old.Step.Pair=step.Pair;old.Step.Inside=step.Inside;rows.Add(old);continue;}
                var row=new SequenceStepRun{Step=step};
                if(old!=null&&old.Begun)
                {
                    row.Activated=row.Begun=true;row.ActivatedAt=old.ActivatedAt;row.StartTime=old.StartTime;row.Actual=old.Actual;row.StartedAt=old.StartedAt;row.Planned=old.Planned;row.Delay=old.Delay;row.LastValid=SequenceNow;
                    row.PauseAcknowledged=old.PauseAcknowledged;row.Due=old.Due;
                    bool sentFact=old.CommandDone&&old.Step.Code.Length>0;
                    row.CommandSince=RuleSeconds();row.CommandSinceUtc=DateTime.UtcNow;row.CommandDone=sentFact||step.Code.Length==0;row.Status=sequencePaused&&step.Row.Keyword=="PAUSE"?"人工暂停":"等待新配置数据";row.Reason="配置已切换，等待新的有效数据";
                    // An already-sent instruction is an immutable fact, never an instruction to resend.
                    if(sentFact){row.Step.Code=old.Step.Code;row.Step.CommandNumber=old.Step.CommandNumber;row.Step.CommandName=old.Step.CommandName;row.Step.SendCode=old.Step.SendCode;row.Step.Destinations=old.Step.Destinations;row.Step.AllDestinations=old.Step.AllDestinations;row.Step.Row.Command=old.Step.Row.Command;row.Step.CommandSignature=old.Step.CommandSignature;}
                    if(!row.CommandDone&&step.Code.Length>0)
                    {
                        bool waitingForResume=step.Row.Keyword=="PAUSE"&&!row.PauseAcknowledged;
                        row.SendPending=definition.Mode=="execute"&&!step.Inside&&!waitingForResume;
                        row.Status=waitingForResume?"人工暂停":row.SendPending?"待发送指令":"等待指令";
                    }
                    ResetSequenceBaseline(row);
                    if(row.CommandDone&&step.Predicate==null&&step.NextDefinition==null&&step.Row.Keyword!="PAUSE")row.Terminal=false;
                }
                rows.Add(row);
            }
            sequenceDefinition=definition;sequenceRows=rows;sequenceDefinition.Rows=rows.Select(r=>r.Step.Row).ToList();
            CurrentSequence.ActiveRows.Clear();foreach(var row in rows.Where(r=>r.Activated&&!r.Terminal))CurrentSequence.ActiveRows.Add(row);
            sequencePosition=rows.FindIndex(r=>!r.Terminal);if(sequencePosition<0)sequencePosition=rows.Count;
            sequenceGroup=sequenceGroupEnd=-1;
            if(sequencePosition<rows.Count&&rows[sequencePosition].Step.Inside)
            {sequenceGroup=steps.FindLastIndex(sequencePosition-1,s=>s.Row.Keyword=="PAR"&&s.Pair>sequencePosition);if(sequenceGroup>=0)sequenceGroupEnd=steps[sequenceGroup].Pair;}
            sequenceDecoderRevision=DecoderRevision;sequenceTCRevision=commandBindingRevision;sequenceSendRevision=sendRevision;sequenceCatalogRevision=Outgoing.Commands.DisplayRevision;Interlocked.Increment(ref CurrentSequence.Generation);
            SequenceAudit("配置切换",null,"运行中","已完成步骤及已发指令保留，当前判据和后续步骤使用新配置");
            foreach(var row in rows.Where(r=>r.Begun&&!r.Terminal&&r.CommandDone&&r.Step.Predicate==null&&r.Step.NextDefinition==null&&r.Step.Row.Keyword!="PAUSE").ToArray())SequenceComplete(row);
            if(sequenceGroup>=0)foreach(var row in rows.Skip(sequenceGroup+1).Take(sequenceGroupEnd-sequenceGroup-1).Where(r=>!r.Activated&&!r.Terminal))ActivateSequenceRow(row,sequencePrevious);
            if(!sequencePaused)SequenceAdvance();
        }
    }
}
