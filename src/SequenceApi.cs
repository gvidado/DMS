using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;

namespace TelemetryMonitor
{
    internal sealed class SequenceImportPreview
    {public string Token,OwnerSession,Revision,EditId;public DateTime Created;public double Timeout;public bool Appendable;public List<SequenceDefinition> Definitions;public List<SequenceConversionEntry> Conversion=new List<SequenceConversionEntry>();}
    public sealed partial class Server
    {
        private Timer sequenceTimer;
        private int sequenceServiceBusy;
        private readonly Dictionary<string,SequenceImportPreview> sequencePreviews=new Dictionary<string,SequenceImportPreview>();
        private readonly Dictionary<string,string> sequenceStartRequests=new Dictionary<string,string>();
        private void PruneSequencePreviews()
        {
            var expired = DateTime.UtcNow.AddMinutes(-10);
            foreach (string token in sequencePreviews.Where(p => p.Value.Created < expired).Select(p => p.Key).ToArray())
                sequencePreviews.Remove(token);
        }

        private void InitializeSequenceService(){sequenceTimer=new Timer(SequenceServiceTick,null,25,25);}
        private void SequenceServiceTick(object ignored)
        {
            if(!running || Interlocked.Exchange(ref sequenceServiceBusy,1)!=0)return;
            try
            {
                lock(commandGate)
                {
                    var work=monitor.SequenceTick(commandTransmitter.SequenceRevision);if(work==null || !monitor.SequenceCanSend(work))return;
                    try
                    {
                        var preview=JsonFile.Decode<Dictionary<string,object>>(JsonFile.Encode(commandTransmitter.Preview("sequence:"+work.RunId,work.Number,work.SendCode,null,work.AllDestinations)));
                        var result=JsonFile.Decode<Dictionary<string,object>>(JsonFile.Encode(commandTransmitter.Send("sequence:"+work.RunId,work.Actor,Convert.ToString(preview["token"]))));
                        monitor.SequenceSent(work,Convert.ToBoolean(result["ok"]),Convert.ToBoolean(result["ok"])?"":JsonFile.Encode(result["rows"]),Convert.ToDouble(result["startedMonotonic"],System.Globalization.CultureInfo.InvariantCulture),JsonFile.Decode<string[]>(JsonFile.Encode(result["eventIds"])));
                    }
                    catch(Exception ex){monitor.SequenceSent(work,false,ex.Message);}
                }
            }
            catch(Exception ex){try{monitor.StopSequence("序列运行异常："+ex.Message);}catch{} }
            finally{Interlocked.Exchange(ref sequenceServiceBusy,0);}
        }
        private bool HandleSequences(HttpListenerContext ctx,Session session)
        {
            string path=ctx.Request.Url.AbsolutePath,method=ctx.Request.HttpMethod;if(!path.StartsWith("/api/sequence/",StringComparison.Ordinal))return false;
            if(HandleSequenceEdits(ctx,session,path,method))return true;
            if(path=="/api/sequence/config" && method=="GET"){Json(ctx.Response,monitor.SequenceConfigurationInfo(),200);return true;}
            if(path=="/api/sequence/diagnostic" && method=="GET")
            {ctx.Response.Headers["Content-Disposition"]="attachment; filename=sequence-diagnostic.json";Json(ctx.Response,monitor.SequenceDiagnostic(Query(ctx.Request,"id"),Query(ctx.Request,"mode"),Query(ctx.Request,"runId")),200);return true;}
            if(path=="/api/sequence/preview/cancel" && method=="POST")
            {
                var body=Body(ctx.Request);string token=Text(body,"token");lock(commandGate)
                {SequenceImportPreview preview;if(sequencePreviews.TryGetValue(token,out preview)&&preview.OwnerSession==session.Token)sequencePreviews.Remove(token);}
                Json(ctx.Response,new{ok=true},200);return true;
            }
            if(path=="/api/sequence/definition" && method=="GET")
            {var definition=monitor.SequenceDefinition(Query(ctx.Request,"id"));var config=JsonFile.Decode<Dictionary<string,object>>(JsonFile.Encode(monitor.SequenceConfigurationInfo()));Json(ctx.Response,monitor.SequenceValidate(definition,Convert.ToDouble(config["defaultTimeout"])),200);return true;}
            if(path=="/api/sequence/tasks" && method=="GET"){Json(ctx.Response,monitor.SequenceTaskList(),200);return true;}
            if(path=="/api/sequence/tasks" && method=="POST")
            {var body=Body(ctx.Request,8*1024*1024);object value;var retain=body.TryGetValue("retain",out value)?JsonFile.Decode<List<string>>(JsonFile.Encode(value)):null;Json(ctx.Response,monitor.SequenceTaskList(Text(body,"activeOnly")=="True",retain),200);return true;}
            if(path=="/api/sequence/state" && method=="GET")
            {int page;if(!Int32.TryParse(Query(ctx.Request,"page"),out page))page=1;Json(ctx.Response,monitor.SequenceState(Query(ctx.Request,"id"),page,Query(ctx.Request,"mode").Length==0?"judge":Query(ctx.Request,"mode"),Query(ctx.Request,"runId")),200);return true;}
            if(path=="/api/sequence/preview" && method=="POST")
            {
                string filename=Path.GetFileName(Query(ctx.Request,"filename")),mode=Query(ctx.Request,"mode"),source=Query(ctx.Request,"sourceIP");double timeout;
                string batchToken=Query(ctx.Request,"batchToken");
                bool convert=Query(ctx.Request,"convert")=="commands";
                if(!filename.EndsWith(".xlsx",StringComparison.OrdinalIgnoreCase))throw new ArgumentException("请选择.xlsx序列文件");if(!Double.TryParse(Query(ctx.Request,"timeout"),System.Globalization.NumberStyles.Float,System.Globalization.CultureInfo.InvariantCulture,out timeout))timeout=0;
                string group=Monitor.CheckSequenceGroup(Query(ctx.Request,"group"));
                var definitions=WorkbookReader.ReadSequences(BinaryBody(ctx.Request),filename,mode);
                foreach(var definition in definitions){definition.SourceIP=source;definition.Group=group;}
                lock(commandGate)
                {
                    SequenceImportPreview preview=null;
                    string revision=monitor.SequenceConfigurationRevision;
                    if(batchToken.Length>0)
                    {
                        if(!sequencePreviews.TryGetValue(batchToken,out preview)||preview.OwnerSession!=session.Token||!preview.Appendable||
                            (DateTime.UtcNow-preview.Created).TotalMinutes>10)
                            throw new InvalidOperationException("批量预览已过期，请重新读取整批文件");
                        if(preview.Revision!=revision||preview.Timeout!=timeout||convert)
                            throw new InvalidOperationException("序列配置已变化，请重新读取整批文件");
                        if(preview.Definitions.Count+definitions.Count>Monitor.MaximumSequences)
                            throw new ArgumentException("加入此文件后，预览序列会超过"+Monitor.MaximumSequences+"条，请分批导入");
                    }
                    var conversion=convert?monitor.ConvertSequenceCommands(definitions):new List<SequenceConversionEntry>();
                    monitor.AnnotateImportedSequenceCommands(definitions);
                    var checkedDefinitions=definitions.Select(d=>monitor.SequenceValidate(d,timeout)).ToArray();
                    if(preview==null)
                    {
                        PruneSequencePreviews();
                        if(sequencePreviews.Count>=16)throw new InvalidOperationException("当前导入预览较多，请稍后再试");
                        preview=new SequenceImportPreview{Token=Auth.RandomToken(),OwnerSession=session.Token,Revision=revision,Timeout=timeout,Definitions=definitions,Conversion=conversion,Appendable=!convert&&Query(ctx.Request,"batch")=="1"};
                        sequencePreviews.Add(preview.Token,preview);
                    }
                    else preview.Definitions.AddRange(definitions);
                    preview.Created=DateTime.UtcNow;
                    // A batch reply carries only the newly read workbook. The token saves all selected definitions together.
                    Json(ctx.Response,new{token=preview.Token,definitions=checkedDefinitions,conversion},200);
                }
                return true;
            }
            if(path=="/api/sequence/recheck" && method=="POST")
            {
                var body=Body(ctx.Request,8*1024*1024);lock(commandGate)
                {
                    SequenceImportPreview previous;
                    if(!sequencePreviews.TryGetValue(Text(body,"token"),out previous) || previous.OwnerSession!=session.Token || (DateTime.UtcNow-previous.Created).TotalMinutes>10)throw new ArgumentException("预览已过期，请重新读取Excel");
                    object input;if(!body.TryGetValue("rows",out input))throw new ArgumentException("缺少预览行内容");
                    var rows=JsonFile.Decode<List<SequenceRow>>(JsonFile.Encode(input));
                    var definitions = new List<SequenceDefinition>(previous.Definitions);
                    int index = definitions.FindIndex(d => d.Id == Text(body, "id"));
                    var definition = index < 0 ? null : JsonFile.Decode<SequenceDefinition>(JsonFile.Encode(definitions[index]));
                    if(definition==null)throw new ArgumentException("工作表不存在，请重新读取或选择序列");
                    if(body.ContainsKey("name"))definition.Name=Text(body,"name").Trim();
                    SequenceRowEditing.Update(definition,rows,Text(body,"nextRowNumber"));
                    monitor.AnnotateImportedSequenceCommands(new[]{definition});
                    definitions[index] = definition;
                    bool singleSheet = Query(ctx.Request, "scope") == "sheet";
                    var checkedDefinitions = (singleSheet ? new[]{definition}.AsEnumerable() : definitions)
                        .Select(d => monitor.SequenceValidate(d, previous.Timeout)).ToArray();
                    previous.Definitions = definitions;
                    previous.Created = DateTime.UtcNow;
                    Json(ctx.Response, new { token = previous.Token, definitions = checkedDefinitions,
                        conversion = singleSheet ? null : previous.Conversion, editId = previous.EditId }, 200);
                }
                return true;
            }
            if(path=="/api/sequence/export-converted" && method=="POST")
            {
                var body=Body(ctx.Request);byte[] bytes;string downloadName;lock(commandGate)
                {
                    SequenceImportPreview preview;
                    if(!sequencePreviews.TryGetValue(Text(body,"token"),out preview) || preview.OwnerSession!=session.Token || (DateTime.UtcNow-preview.Created).TotalMinutes>10)throw new ArgumentException("预览已过期，请重新读取Excel");
                    bytes=WorkbookReader.ExportConvertedSequences(preview.Definitions);
                    var definition=preview.Definitions.FirstOrDefault();string stem=Path.GetFileNameWithoutExtension(definition==null?"":definition.Filename??"");
                    downloadName=(String.IsNullOrWhiteSpace(stem)?"序列":stem)+"_判读格式匹配.xlsx";
                }
                ctx.Response.ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";ctx.Response.Headers["Content-Disposition"]="attachment; filename=converted-sequences.xlsx; filename*=UTF-8''"+Uri.EscapeDataString(downloadName);ctx.Response.ContentLength64=bytes.Length;ctx.Response.OutputStream.Write(bytes,0,bytes.Length);return true;
            }
            if(path=="/api/sequence/apply" && method=="POST")
            {
                var body=Body(ctx.Request);lock(commandGate)
                {
                    SequenceImportPreview preview;if(!sequencePreviews.TryGetValue(Text(body,"token"),out preview)||preview.OwnerSession!=session.Token||(DateTime.UtcNow-preview.Created).TotalMinutes>10)throw new ArgumentException("预览已过期，请重新读取Excel");
                    if(!String.IsNullOrEmpty(preview.EditId))throw new ArgumentException("已入库序列请使用保存序列修改，不能作为新导入应用");
                    object input;if(!body.TryGetValue("ids",out input))throw new ArgumentException("请选择要保存的工作表");var ids=JsonFile.Decode<string[]>(JsonFile.Encode(input));if(ids==null||ids.Length==0||ids.Distinct().Count()!=ids.Length||ids.Any(id=>!preview.Definitions.Any(d=>d.Id==id)))throw new ArgumentException("工作表选择无效");
                    string mode=Text(body,"mode");if(mode!="add"&&mode!="replace")throw new ArgumentException("请选择添加或替换");
                    var selected=new HashSet<string>(ids,StringComparer.Ordinal);
                    var groups=new Dictionary<string,string>(StringComparer.Ordinal);
                    if(body.TryGetValue("groups",out input))
                    {
                        var assignments=input as Dictionary<string,object>;
                        if(assignments==null || assignments.Count>selected.Count)throw new ArgumentException("序列分组选择无效");
                        foreach(var pair in assignments)
                        {
                            if(!selected.Contains(pair.Key) || !(pair.Value is string))throw new ArgumentException("序列分组选择无效");
                            groups.Add(pair.Key,Monitor.CheckSequenceGroup((string)pair.Value));
                        }
                    }
                    monitor.SaveSequences(preview.Definitions.Where(d=>selected.Contains(d.Id)).ToList(),mode=="replace",preview.Revision,preview.Timeout,groups);
                    sequencePreviews.Remove(preview.Token);Json(ctx.Response,monitor.SequenceConfigurationInfo(),200);
                }
                return true;
            }
            if(path=="/api/sequence/config" && method=="POST")
            {
                var body=Body(ctx.Request);double timeout;if(!Double.TryParse(Text(body,"defaultTimeout"),System.Globalization.NumberStyles.Float,System.Globalization.CultureInfo.InvariantCulture,out timeout))throw new ArgumentException("请填写有效默认超时");
                lock(commandGate)monitor.EditSequence(Text(body,"id"),Text(body,"mode"),Text(body,"sourceIP"),timeout,Text(body,"revision"),Text(body,"delete")=="true");Json(ctx.Response,monitor.SequenceConfigurationInfo(),200);return true;
            }
            if(path=="/api/sequence/control" && method=="POST")
            {
                var body=Body(ctx.Request);lock(commandGate)
                {
                    string action=Text(body,"action"),runId=Text(body,"runId"),controlMode=Text(body,"mode");
                    if(action=="start")
                    {
                        string id=Text(body,"id");
                        string requestId=Text(body,"requestId");if(!System.Text.RegularExpressions.Regex.IsMatch(requestId,@"^[a-f0-9]{32}$"))throw new ArgumentException("启动请求标识无效，请刷新页面");string requestKey=session.Username+":"+requestId,oldRun;
                        if(sequenceStartRequests.TryGetValue(requestKey,out oldRun)){Json(ctx.Response,monitor.SequenceSummaryForRun(oldRun),200);return true;}
                        controlMode=monitor.SequenceDefinition(id).Mode;
                        var saved=JsonFile.Decode<Dictionary<string,object>>(JsonFile.Encode(monitor.SequenceConfigurationInfo()));if(Text(body,"revision")!=Convert.ToString(saved["revision"]))throw new InvalidOperationException("序列配置已变化，请重新载入后启动");
                        var start=Monitor.ParseSequenceStart(Text(body,"startRow"),Text(body,"t0Elapsed"),Text(body,"f0Elapsed"));
                        string runName=Monitor.CheckSequenceRunName(Text(body,"runName"));var steps=monitor.PrepareSequence(id,start);
                        foreach(var step in Monitor.SequenceJumpSteps(steps.Skip(Monitor.SequenceStartIndex(steps,start))).Where(s=>s.Mode=="execute"&&!s.Inside&&s.Code.Length>0))commandTransmitter.CheckSequenceCommand(step);
                        runId=monitor.StartSequence(id,steps,auth.User(session).DisplayName+"（"+session.Username+"）",commandTransmitter.SequenceRevision,runName,start);
                        var started=JsonFile.Decode<Dictionary<string,object>>(JsonFile.Encode(monitor.SequenceSummaryForRun(runId)));sequenceStartRequests[requestKey]=Convert.ToString(started["runId"]);
                        if(sequenceStartRequests.Count>256)sequenceStartRequests.Remove(sequenceStartRequests.Keys.First(k=>k!=requestKey));
                    }
                    else if(action=="stop")
                    {controlMode=monitor.SequenceModeForRun(runId);monitor.StopSequenceRun(runId,"已人工停止本任务；其他任务、监听和常态化判读继续");}
                    else if(action=="pause" || action=="resume")
                    {controlMode=monitor.SequenceModeForRun(runId);var info=JsonFile.Decode<Dictionary<string,object>>(JsonFile.Encode(monitor.SequenceSummaryForRun(runId)));if(Text(body,"controlRevision")!=Convert.ToString(info["controlRevision"]))throw new InvalidOperationException("暂停状态已变化，请刷新后操作");if(action=="pause")monitor.PauseSequence(runId);else monitor.ResumeSequence(runId);}
                    else throw new ArgumentException("未知序列操作");Json(ctx.Response,monitor.SequenceSummaryForRun(runId),200);
                }
                return true;
            }
            if(path=="/api/sequence/runs" && method=="GET")
            {int page;if(!Int32.TryParse(Query(ctx.Request,"page"),out page))page=1;Json(ctx.Response,monitor.SequenceRunsPage(page,Query(ctx.Request,"search"),Query(ctx.Request,"mode")),200);return true;}
            if(path=="/api/sequence/history" && method=="GET")
            {int page;if(!Int32.TryParse(Query(ctx.Request,"page"),out page))page=1;string result=Query(ctx.Request,"result");Json(ctx.Response,monitor.SequenceHistoryPage(page,Query(ctx.Request,"runId"),Query(ctx.Request,"search"),Query(ctx.Request,"mode"),result.Length==0?"all":result,Query(ctx.Request,"since")),200);return true;}
            if(path=="/api/sequence/export.csv" && method=="GET")
            {string result=Query(ctx.Request,"result");ctx.Response.ContentType="text/csv; charset=utf-8";ctx.Response.SendChunked=true;ctx.Response.Headers["Content-Disposition"]="attachment; filename=sequence-results.csv";using(var writer=new StreamWriter(ctx.Response.OutputStream,new UTF8Encoding(true),65536,true))monitor.ExportSequenceHistory(writer,Query(ctx.Request,"runId"),Query(ctx.Request,"search"),Query(ctx.Request,"mode"),result.Length==0?"all":result);return true;}
            Json(ctx.Response,new{error="序列接口不存在"},404);return true;
        }
    }
}
