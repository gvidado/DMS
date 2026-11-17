using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;

namespace TelemetryMonitor
{
    internal sealed class ContinuousPreview
    {
        public string Token,Username,Revision,DecoderRevision,SourceSignature,Filename;
        public DateTime Created;
        public int Total;
        public List<ContinuousRule> Rules=new List<ContinuousRule>();
        public List<RuleImportError> Errors=new List<RuleImportError>();
    }
    public sealed partial class Server
    {
        private readonly Dictionary<string,ContinuousPreview> continuousPreviews=new Dictionary<string,ContinuousPreview>();
        private ContinuousPreview RulePreview(string token,Session session)
        {ContinuousPreview p;if(!continuousPreviews.TryGetValue(token??"",out p) || p.Username!=session.Username || (DateTime.UtcNow-p.Created).TotalMinutes>10)throw new ArgumentException("规则预览已过期或不属于当前账号，请重新读取");return p;}
        private string ContinuousKey(ContinuousRule r){return monitor.ContinuousRuleKey(r);}
        private List<ContinuousRule> PlanRules(ContinuousPreview p,string mode,bool disabled,out int duplicates,out List<string> conflicts)
        {
            if(mode!="add" && mode!="replace")throw new ArgumentException("请选择添加或替换");
            var incoming=JsonFile.Decode<List<ContinuousRule>>(JsonFile.Encode(p.Rules));if(disabled)foreach(var r in incoming)r.Enabled=false;
            conflicts=new List<string>();duplicates=0;
            if(mode=="replace")return incoming;
            var current=monitor.ContinuousSnapshot().Rules;var keys=current.ToDictionary(ContinuousKey);
            foreach(var r in incoming)
            {var initial=current.FirstOrDefault(existing=>(Monitor.IsUnconfiguredRule(existing) || Monitor.IsInitialTemplateRule(existing)) && existing.ParameterId==r.ParameterId && (String.IsNullOrEmpty(existing.SourceIP) || existing.SourceIP==r.SourceIP));
                if(initial!=null){if(!Monitor.IsDefaultRule(initial)){conflicts.Add(r.Code+"的初始配置已经修改，添加不会覆盖，请编辑已有规则或使用替换");continue;}keys.Remove(ContinuousKey(initial));current.Remove(initial);current.Add(r);keys[ContinuousKey(r)]=r;continue;}
                ContinuousRule old;if(keys.TryGetValue(ContinuousKey(r),out old)){if(Monitor.ContinuousDefinition(old)==Monitor.ContinuousDefinition(r))duplicates++;else conflicts.Add(r.Code+"的规则与已有内容不同；添加不会覆盖，请编辑已有规则或使用替换");}
                else {current.Add(r);keys[ContinuousKey(r)]=r;}}
            return current;
        }
        private bool HandleContinuousRules(HttpListenerContext ctx,Session session)
        {
            string path=ctx.Request.Url.AbsolutePath,method=ctx.Request.HttpMethod;if(!path.StartsWith("/api/continuous-rules",StringComparison.Ordinal))return false;
            if(HandleRuleMigration(ctx,session,path,method))return true;
            if(path=="/api/continuous-rules/abnormals" && method=="GET")
            {
                int page=1,size=50;long through=0;string value=Query(ctx.Request,"page");if(value.Length>0 && !Int32.TryParse(value,out page))throw new ArgumentException("页码无效");value=Query(ctx.Request,"pageSize");if(value.Length>0 && !Int32.TryParse(value,out size))throw new ArgumentException("分页数量无效");value=Query(ctx.Request,"throughId");if(value.Length>0 && !Int64.TryParse(value,out through))throw new ArgumentException("记录游标无效");
                Json(ctx.Response,monitor.JudgmentAbnormalPage(page,size,Query(ctx.Request,"status")==""?"all":Query(ctx.Request,"status"),through),200);return true;
            }
            if(path=="/api/continuous-rules/abnormals/release" && method=="POST")
            {
                var body=Body(ctx.Request);long id;if(!Int64.TryParse(Text(body,"id"),out id))throw new ArgumentException("记录号无效");lock(commandGate)monitor.ReleaseJudgmentAbnormal(id,Text(body,"reason"),session.Username,auth.User(session).DisplayName);
                Json(ctx.Response,new{ok=true},200);return true;
            }
            if(path=="/api/continuous-rules/abnormals/export.csv" && method=="GET")
            {
                ctx.Response.ContentType="text/csv; charset=utf-8";ctx.Response.SendChunked=true;ctx.Response.Headers["Content-Disposition"]="attachment; filename=judgment-abnormal-history.csv";
                using(var writer=new StreamWriter(ctx.Response.OutputStream,new UTF8Encoding(true),65536,true))monitor.ExportJudgmentAbnormal(writer);return true;
            }
            if(path=="/api/continuous-rules/globals" && method=="GET"){Json(ctx.Response,monitor.SharedVariableList(),200);return true;}
            if(path=="/api/continuous-rules/globals" && method=="POST")
            {
                var body=Body(ctx.Request);lock(commandGate)monitor.SetSharedVariable(Text(body,"name"),Text(body,"kind"),Text(body,"value"),Text(body,"revision"),Text(body,"generation"),auth.User(session).DisplayName+"（"+session.Username+"）");
                Json(ctx.Response,monitor.SharedVariableList(),200);return true;
            }
            if(path=="/api/continuous-rules/parameters" && method=="GET"){Json(ctx.Response,monitor.ContinuousParameters(),200);return true;}
            if(path=="/api/continuous-rules/source" && method=="GET"){Json(ctx.Response,monitor.ContinuousSource(Query(ctx.Request,"parameterId")),200);return true;}
            if(path=="/api/continuous-rules" && method=="GET")
            {
                object result=monitor.ContinuousList();
                if(configurationContext!=null){var draft=JsonFile.Decode<Dictionary<string,object>>(JsonFile.Encode(result));var effective=JsonFile.Decode<Dictionary<string,object>>(JsonFile.Encode(liveMonitor.ContinuousList()));draft["execution"]=effective["execution"];draft["effectiveEnabled"]=effective["enabled"];result=draft;}
                Json(ctx.Response,result,200);return true;
            }
            if(path=="/api/continuous-rules/control" && method=="POST")
            {
                string action=Text(Body(ctx.Request),"action");lock(commandGate){if(action=="start")monitor.StartJudgment();else if(action=="stop")monitor.StopJudgment();else throw new ArgumentException("未知判读操作");}
                Json(ctx.Response,monitor.JudgmentInfo(),200);return true;
            }
            if(path=="/api/continuous-rules/tc" && method=="GET")
            {int page;if(!Int32.TryParse(Query(ctx.Request,"page"),out page))page=1;Json(ctx.Response,monitor.CommandBindingList(page,Query(ctx.Request,"search")),200);return true;}
            if(path=="/api/continuous-rules/tc" && method=="POST")
            {var body=Body(ctx.Request);long number;if(!Int64.TryParse(Text(body,"number"),out number))throw new ArgumentException("指令记录号无效");lock(commandGate)monitor.SaveCommandBinding(number,Text(body,"code"),Text(body,"revision"));Json(ctx.Response,new{ok=true},200);return true;}
            if(path=="/api/continuous-rules/tc/detail" && method=="GET")
            {long number;if(!Int64.TryParse(Query(ctx.Request,"number"),out number))throw new ArgumentException("指令记录号无效");Json(ctx.Response,monitor.CommandBindingDetail(number),200);return true;}
            if(path=="/api/continuous-rules/export.csv" && method=="GET")
            {
                var snapshot=monitor.ContinuousSnapshot();
                ctx.Response.ContentType="text/csv; charset=utf-8";ctx.Response.SendChunked=true;
                ctx.Response.Headers["Content-Disposition"]="attachment; filename=continuous-rules.csv";
                using(var writer=new StreamWriter(ctx.Response.OutputStream,new UTF8Encoding(true),65536,true))
                {
                    writer.NewLine="\r\n";
                    writer.WriteLine("序号,参数代号,参数名称,判读级别,是否判读,判读规则");
                    int number=0;
                    foreach(var rule in snapshot.Rules)
                        writer.WriteLine(String.Join(",",new[]{(++number).ToString(System.Globalization.CultureInfo.InvariantCulture),
                            Monitor.CsvCell(rule.Code),Monitor.CsvCell(rule.Name),Monitor.CsvCell(rule.Level),
                            Monitor.CsvCell(rule.Enabled?"是":"否"),Monitor.CsvCell(rule.Script)}));
                }
                return true;
            }
            if(path=="/api/continuous-rules/export" && method=="GET"){ctx.Response.Headers["Content-Disposition"]="attachment; filename=continuous-rules.json";Json(ctx.Response,monitor.ContinuousSnapshot(),200);return true;}
            if(path=="/api/continuous-rules/preview" && method=="POST")
            {
                DateTime requestedAt=DateTime.UtcNow;
                string name=Path.GetFileName(Query(ctx.Request,"filename"));if(!name.EndsWith(".xlsx",StringComparison.OrdinalIgnoreCase))throw new ArgumentException("请选择.xlsx规则文件");
                var raw=WorkbookReader.ReadRules(BinaryBody(ctx.Request),name);
                lock(commandGate)lock(monitor.Gate)
                {
                    foreach(string key in continuousPreviews.Where(entry=>(entry.Value.Username==session.Username&&entry.Value.Created<=requestedAt) || (DateTime.UtcNow-entry.Value.Created).TotalMinutes>10).Select(entry=>entry.Key).ToArray())continuousPreviews.Remove(key);
                    if(continuousPreviews.Count>=16)throw new InvalidOperationException("当前预览较多，请稍后再试");
                    var p=new ContinuousPreview{Token=Auth.RandomToken(),Username=session.Username,Created=DateTime.UtcNow,Revision=monitor.ContinuousSnapshot().Revision,DecoderRevision=monitor.DecoderRevision,
                        SourceSignature=JsonFile.Encode(monitor.Config.SourceIPs),Filename=name,Total=raw.Count};
                    foreach(var r in raw)
                    {
                        r.SourceIP=Query(ctx.Request,"sourceIP");string error=String.IsNullOrWhiteSpace(r.Script)?"判读规则为空，Excel只导入可执行规则":monitor.CheckContinuousRule(r,true);
                        if(error.Length>0)p.Errors.Add(new RuleImportError{Sheet=r.SourceSheet,Row=r.SourceRow,Code=r.Code,Message=error});else p.Rules.Add(r);
                    }
                    foreach(var group in p.Rules.GroupBy(ContinuousKey).Where(g=>g.Count()>1).ToArray())
                        foreach(var r in group){p.Errors.Add(new RuleImportError{Sheet=r.SourceSheet,Row=r.SourceRow,Code=r.Code,Message="本次文件中同一参数和来源重复，请修正后重新读取"});p.Rules.Remove(r);}
                    continuousPreviews[p.Token]=p;int duplicates;List<string> conflicts;
                    var add=PlanRules(p,"add",false,out duplicates,out conflicts);
                    Json(ctx.Response,new{token=p.Token,revision=p.Revision,total=p.Total,accepted=p.Rules.Count,rejected=p.Errors.Count,rules=p.Rules,errors=p.Errors,
                        duplicates,conflicts,addTotal=add.Count,currentCount=monitor.ContinuousSnapshot().Rules.Count},200);
                }
                return true;
            }
            if(path=="/api/continuous-rules/preview/cancel" && method=="POST")
            {
                var body=Body(ctx.Request);string token=Text(body,"token");lock(commandGate)
                {ContinuousPreview preview;if(continuousPreviews.TryGetValue(token,out preview)&&preview.Username==session.Username)continuousPreviews.Remove(token);}
                Json(ctx.Response,new{ok=true},200);return true;
            }
            if(path=="/api/continuous-rules/errors" && method=="GET")
            {
                ContinuousPreview p;lock(commandGate)p=RulePreview(Query(ctx.Request,"token"),session);
                ctx.Response.ContentType="text/csv; charset=utf-8";ctx.Response.Headers["Content-Disposition"]="attachment; filename=rule-import-errors.csv";
                using(var writer=new StreamWriter(ctx.Response.OutputStream,new UTF8Encoding(true),65536,true))
                {writer.WriteLine("工作表,行号,参数代号,错误原因");foreach(var e in p.Errors)writer.WriteLine(String.Join(",",new[]{e.Sheet,e.Row.ToString(),e.Code,e.Message}.Select(v=>{string value=v??"";if(value.Length>0 && "=+-@\t\r".IndexOf(value[0])>=0)value="'"+value;return "\""+value.Replace("\"","\"\"")+"\"";})));}
                return true;
            }
            if(path=="/api/continuous-rules/apply" && method=="POST")
            {
                var body=Body(ctx.Request);
                lock(commandGate)lock(monitor.Gate)
                {
                    var p=RulePreview(Text(body,"token"),session);
                    if(p.Revision!=monitor.ContinuousSnapshot().Revision || p.DecoderRevision!=monitor.DecoderRevision || p.SourceSignature!=JsonFile.Encode(monitor.Config.SourceIPs))throw new InvalidOperationException("参数或规则配置已变化，请重新读取预览");
                    if(p.Rules.Count==0)throw new ArgumentException("没有可执行规则，未修改当前配置");
                    bool disabled=Text(body,"enablePolicy")=="disabled";if(!disabled && Text(body,"enablePolicy")!="excel")throw new ArgumentException("请选择按Excel启用或全部停用");
                    int duplicates;List<string> conflicts;var rows=PlanRules(p,Text(body,"mode"),disabled,out duplicates,out conflicts);
                    if(conflicts.Count>0)throw new ArgumentException(String.Join("；",conflicts.Take(5)));
                    if(Text(body,"mode")=="add" && CanonicalJson.Equivalent(rows,monitor.ContinuousSnapshot().Rules))throw new ArgumentException("没有可添加的新规则，相同内容已存在");
                    monitor.SaveContinuousRules(rows,p.Revision);continuousPreviews.Remove(p.Token);Json(ctx.Response,new{ok=true,total=rows.Count,skipped=p.Errors.Count},200);
                }
                return true;
            }
            if((path=="/api/continuous-rules/save" || path=="/api/continuous-rules/toggle" || path=="/api/continuous-rules/delete") && method=="POST")
            {
                var body=Body(ctx.Request,128*1024);
                lock(commandGate)lock(monitor.Gate)
                {
                    var config=monitor.ContinuousSnapshot();string id=Text(body,"id");var row=config.Rules.FirstOrDefault(r=>r.Id==id);
                    if(path.EndsWith("/save"))
                    {
                        object item;if(!body.TryGetValue("rule",out item))throw new ArgumentException("缺少规则内容");
                        var edit=JsonFile.Decode<ContinuousRule>(JsonFile.Encode(item));if(edit==null)throw new ArgumentException("规则内容无效");
                        row=config.Rules.FirstOrDefault(r=>r.Id==edit.Id);if(row==null && edit.Id.Length>0)throw new ArgumentException("规则已删除，请刷新");
                        edit.Id=row==null?Guid.NewGuid().ToString("N"):row.Id;edit.ModifiedAt=BeijingTime.Now.ToString("o");
                        string error=monitor.CheckContinuousRule(edit,true,true);if(error.Length>0)throw new ArgumentException(error);
                        if(row==null)config.Rules.Add(edit);else config.Rules[config.Rules.IndexOf(row)]=edit;
                    }
                    else
                    {
                        if(row==null)throw new ArgumentException("规则已不存在");
                        if(path.EndsWith("/delete"))config.Rules.Remove(row);
                        else {string enabled=Text(body,"enabled");if(enabled!="True" && enabled!="False" && enabled!="true" && enabled!="false")throw new ArgumentException("启用状态无效");row.Enabled=Boolean.Parse(enabled);row.ModifiedAt=BeijingTime.Now.ToString("o");}
                    }
                    monitor.SaveContinuousRules(config.Rules,Text(body,"revision"));Json(ctx.Response,new{ok=true},200);
                }
                return true;
            }
            Json(ctx.Response,new{error="常态化判读接口不存在"},404);return true;
        }
    }
}
