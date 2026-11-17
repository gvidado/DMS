using System;
using System.Linq;

namespace TelemetryMonitor
{
    public sealed partial class ObservedCommands
    {
        internal CommandImportPlan PreviewClearCategory(string kind,long expectedRevision)
        {
            if(!new[]{"BC","PSS","KC","YKE","YKM","ZK"}.Contains(kind))throw new ArgumentException("请选择要清空的指令类别");
            lock(gate)
            {
                if(!ready)throw new InvalidOperationException(error);
                if(expectedRevision!=revision)throw new InvalidOperationException("指令库已变化，请重新确认");
                var copy=Serializer().Deserialize<ObservedCommandConfiguration>(Serializer().Serialize(configuration));
                foreach(var entry in copy.Commands.Where(c=>c.Categories.Any(k=>k.Kind==kind)).ToArray())
                {
                    var other=entry.DestinationIPs.Where(ip=>!entry.Categories.Any(c=>c.DestinationIPs.Contains(ip))).ToArray();
                    entry.Categories.RemoveAll(c=>c.Kind==kind);
                    entry.DestinationIPs=entry.Categories.SelectMany(c=>c.DestinationIPs).Concat(other).Distinct().ToList();
                    if(entry.DestinationIPs.Count==0 || entry.STC&&entry.Categories.Count==0)copy.Commands.Remove(entry);
                    else if(!entry.DestinationIPs.Contains(entry.DestinationIP))entry.DestinationIP=entry.DestinationIPs[0];
                }
                return new CommandImportPlan{Revision=revision,Configuration=copy};
            }
        }
    }
    public sealed partial class Monitor
    {
        internal object CommandCategoryClearPreview(string kind)
        {
            if(String.IsNullOrEmpty(kind))return CommandClearPreview();
            lock(Gate)
            {
                var plan=Outgoing.Commands.PreviewClearCategory(kind,Outgoing.Commands.DisplayRevision);
                var removed=Outgoing.Commands.BindingEntries().Where(c=>c.Categories.Any(k=>k.Kind==kind)).ToArray();
                var codes=removed.SelectMany(c=>c.Categories.Where(k=>k.Kind==kind).Select(k=>k.Code)).ToArray();
                int bindings=tcBindings.Bindings.Count(b=>codes.Contains(b.Code)||!plan.Configuration.Commands.Any(c=>c.MatchesNumber(b.Number)));
                return new{revision=plan.Revision,tcRevision=commandBindingRevision,kind,count=removed.Length,bindings};
            }
        }
        internal void ClearCommandCategory(string kind,long revision,string tcRevision)
        {
            if(String.IsNullOrEmpty(kind)){ClearCommandCatalog(revision,tcRevision);return;}
            lock(Gate)
            {
                EnsureSequenceIdle();
                if(Listening||Demo||Outgoing.IsActive||judgmentRunning)throw new InvalidOperationException("请先停止接收和判读，再清空指令类别");
                if(tcStorageError.Length>0)throw new InvalidOperationException(tcStorageError);
                if(tcRevision!=commandBindingRevision)throw new InvalidOperationException("TC绑定已变化，请重新确认");
                var removedCodes=Outgoing.Commands.BindingEntries().SelectMany(c=>c.Categories.Where(k=>k.Kind==kind).Select(k=>k.Code)).ToArray();
                var plan=Outgoing.Commands.PreviewClearCategory(kind,revision);
                var previous=tcBindings;
                var next=SequenceClone(tcBindings);next.Revision=Guid.NewGuid().ToString("N");
                next.Bindings.RemoveAll(b=>removedCodes.Contains(b.Code)||!plan.Configuration.Commands.Any(c=>c.MatchesNumber(b.Number)));
                JsonFile.Save(TCPath,next);
                try{Outgoing.Commands.ApplyCsv(plan);}
                catch{try{JsonFile.Save(TCPath,previous);}catch(Exception ex){tcStorageError="清空失败且TC绑定恢复失败："+ex.Message;}throw;}
                tcBindings=next;commandBindingRevision=next.Revision;ruleBindings.Clear();
            }
        }
    }
}
