using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;

namespace TelemetryMonitor
{
    public sealed partial class Server
    {
        private bool HandleSequenceEdits(HttpListenerContext ctx,Session session,string path,string method)
        {
            if(path=="/api/sequence/edit-preview" && method=="POST")
            {
                var body=Body(ctx.Request);
                lock(commandGate)
                {
                    var config=JsonFile.Decode<Dictionary<string,object>>(JsonFile.Encode(monitor.SequenceConfigurationInfo()));
                    string revision=Convert.ToString(config["revision"]);
                    if(Text(body,"revision")!=revision)throw new InvalidOperationException("序列配置已变化，请重新载入再编辑");
                    var definition=monitor.SequenceDefinition(Text(body,"id"));
                    PruneSequencePreviews();
                    if(sequencePreviews.Count>=16)throw new InvalidOperationException("当前编辑预览较多，请稍后再试");
                    var preview=new SequenceImportPreview{Token=Auth.RandomToken(),OwnerSession=session.Token,Revision=revision,EditId=definition.Id,Created=DateTime.UtcNow,Timeout=Convert.ToDouble(config["defaultTimeout"]),Definitions=new List<SequenceDefinition>{definition}};
                    sequencePreviews[preview.Token]=preview;
                    Json(ctx.Response,new{token=preview.Token,editId=preview.EditId,definitions=new[]{monitor.SequenceValidate(definition,preview.Timeout)},conversion=preview.Conversion},200);
                }
                return true;
            }
            if(path=="/api/sequence/save-edits" && method=="POST")
            {
                var body=Body(ctx.Request);
                lock(commandGate)
                {
                    SequenceImportPreview preview;
                    if(!sequencePreviews.TryGetValue(Text(body,"token"),out preview) || preview.OwnerSession!=session.Token || (DateTime.UtcNow-preview.Created).TotalMinutes>10)throw new ArgumentException("编辑预览已过期，请重新选择已保存序列");
                    if(String.IsNullOrEmpty(preview.EditId) || preview.Definitions.Count!=1 || preview.Definitions[0].Id!=preview.EditId)throw new ArgumentException("请选择已入库序列进行修改");
                    object group;
                    if(body.TryGetValue("group",out group) && !(group is string))throw new ArgumentException("序列分组名称无效");
                    monitor.SaveSequenceEdits(preview.Definitions[0],preview.Revision,(string)group);
                    sequencePreviews.Remove(preview.Token);Json(ctx.Response,monitor.SequenceConfigurationInfo(),200);
                }
                return true;
            }
            return false;
        }
    }
    public sealed partial class Monitor
    {
        internal void SaveSequenceEdits(SequenceDefinition edited,string revision,string group)
        {
            lock(Gate)
            {
                EnsureSequenceIdle();if(sequenceStorageError.Length>0)throw new InvalidOperationException(sequenceStorageError);
                if(revision!=sequenceConfiguration.Revision)throw new InvalidOperationException("序列配置已被其他操作修改。当前预览仍保留，可先复制修改内容，再重新载入最新配置。");
                var copy=SequenceClone(sequenceConfiguration);var definition=copy.Sequences.FirstOrDefault(d=>d.Id==edited.Id);
                if(definition==null)throw new ArgumentException("序列已不存在");
                string name=(edited.Name??"").Trim();
                if(copy.Sequences.Any(d=>d.Id!=definition.Id && d.Mode==definition.Mode && d.Name==name))throw new ArgumentException("同模式已存在相同名称的序列："+name);
                // Keep the saved identity, sequence code, mode, source and workbook provenance.
                definition.Name=name;definition.Rows=SequenceClone(edited.Rows);definition.NextRowNumber=Math.Max(definition.NextRowNumber,edited.NextRowNumber);
                if(group!=null)definition.Group=CheckSequenceGroup(group);
                CheckSequenceConfig(copy);
                foreach(var item in copy.Sequences)
                {
                    List<string> errors,warnings;CompileSequence(item,copy.DefaultTimeoutSeconds,out errors,out warnings,context:copy);
                    if(errors.Count>0)throw new ArgumentException(item.Code+"："+String.Join("；",errors.Take(30)));
                }
                copy.Revision=Guid.NewGuid().ToString("N");JsonFile.Save(SequenceConfigPath,copy);sequenceConfiguration=copy;
            }
        }
    }
}
