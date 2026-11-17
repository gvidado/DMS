using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;

namespace TelemetryMonitor
{
    public sealed partial class Monitor
    {
        private static string SourceParameterId(string id)
        {id=id??"";int split=id.IndexOf("::",StringComparison.Ordinal);if(split<0)return id;id=id.Substring(split+2);return id.StartsWith("TME::")||id.StartsWith("TMB::")?id.Substring(5):id;}
        private static Parameter[] MigrationFields(ContinuousRule rule,IEnumerable<Parameter> fields)
        {
            string source=SourceParameterId(rule.ParameterId);
            var matches=fields.Where(p=>p.SourceId==source).ToArray();
            return matches.Length>0?matches:fields.Where(p=>TelemetryReferences.CodeOf(p.Code)==TelemetryReferences.CodeOf(rule.Code)).ToArray();
        }
        private static void MarkRuleMigration(ContinuousRule rule,string reason)
        {rule.MigrationOriginalEnabled=rule.Enabled;rule.Enabled=false;rule.MigrationPending=true;rule.MigrationReason=reason;rule.ModifiedAt=BeijingTime.Now.ToString("o");}
        private static ContinuousRule CreateMigratedRule(ContinuousRule old,Parameter target,List<Parameter> fields)
        {
            var result=JsonFile.Decode<ContinuousRule>(JsonFile.Encode(old));
            result.Code=target.Reference;result.ParameterId=target.Id;result.MigrationPending=false;result.MigrationReason="";
            Func<string,string> translate=name=>
            {
                if(!name.StartsWith("TM.",StringComparison.Ordinal))return name;
                string raw=name.Substring(3);var matches=fields.Where(p=>p.Reference==target.TelemetryNamespace+"."+raw).ToArray();
                if(matches.Length!=1)throw new ArgumentException("无法唯一确定"+target.TelemetryNamespace+"."+raw+"，请补齐映射或在迁移预览中修改引用");
                return matches[0].Reference;
            };
            result.Script=Regex.Replace(old.Script??"",@"//[^\r\n]*|[\p{L}_][\p{L}\p{Nd}_.]*",m=>
            {
                if(!m.Value.StartsWith("TM.",StringComparison.Ordinal))return m.Value;
                var parts=m.Value.Split('.');return translate(parts[0]+"."+parts[1])+(parts.Length>2?"."+String.Join(".",parts.Skip(2)):"");
            });
            result.Bindings=new Dictionary<string,RuleParameterBinding>();
            foreach(var item in old.Bindings??new Dictionary<string,RuleParameterBinding>())
            {
                string name=translate(item.Key);var field=fields.SingleOrDefault(p=>p.Reference==name);
                if(field==null)throw new ArgumentException("附加绑定尚无可用映射："+name);
                result.Bindings[name]=new RuleParameterBinding{ParameterId=field.Id,SourceIP=item.Value.SourceIP};
            }
            RuleParser.Compile(result.Script);result.ModifiedAt=BeijingTime.Now.ToString("o");return result;
        }
        internal object RuleMigrationChoices(string id)
        {
            lock(Gate)
            {
                var old=continuous.Rules.SingleOrDefault(r=>r.Id==id&&r.MigrationPending);if(old==null)throw new ArgumentException("待迁移规则已不存在，请刷新");
                var fields=MigrationFields(old,Catalog);
                return new{revision=continuous.Revision,decoderRevision=DecoderRevision,rule=old,
                    targets=fields.Select(p=>new{id=p.Id,reference=p.Reference,name=p.Name,packet=p.PacketName,sourceIP=decoder.Packets.Single(d=>d.Id==p.PacketId).SourceIP}).ToArray()};
            }
        }
        internal object PreviewRuleMigration(string id,string[] targets)
        {
            lock(Gate)
            {
                var old=continuous.Rules.SingleOrDefault(r=>r.Id==id&&r.MigrationPending);if(old==null)throw new ArgumentException("待迁移规则已不存在，请刷新");
                if(targets==null||targets.Length==0||targets.Length>16||targets.Distinct().Count()!=targets.Length)throw new ArgumentException("请选择1～16个不同迁移目标");
                var allowed=MigrationFields(old,Catalog);var rows=new List<object>();
                foreach(string targetId in targets)
                {
                    var target=allowed.SingleOrDefault(p=>p.Id==targetId);if(target==null)throw new ArgumentException("迁移目标已变化，请刷新");
                    ContinuousRule copy;string error="";
                    try{copy=CreateMigratedRule(old,target,Catalog);}
                    catch(Exception ex){copy=JsonFile.Decode<ContinuousRule>(JsonFile.Encode(old));copy.Code=target.Reference;copy.ParameterId=target.Id;copy.MigrationPending=false;copy.MigrationReason="";error=ex.Message;}
                    copy.Enabled=false;copy.Id="";copy.MigrationOriginalEnabled=false;
                    if(error.Length==0)error=CheckContinuousRule(copy,false,true);
                    rows.Add(new{rule=copy,reference=target.Reference,packet=target.PacketName,error});
                }
                return new{id,revision=continuous.Revision,decoderRevision=DecoderRevision,rows};
            }
        }
        internal void ApplyRuleMigration(string id,string revision,string decoderRevision,List<ContinuousRule> rows)
        {
            lock(Gate)
            {
                if(decoderRevision!=DecoderRevision)throw new InvalidOperationException("SPP或解码表已改变，请重新预览迁移");
                var old=continuous.Rules.SingleOrDefault(r=>r.Id==id&&r.MigrationPending);if(old==null)throw new ArgumentException("待迁移规则已不存在，请刷新");
                if(rows==null||rows.Count==0||rows.Count>16||rows.Any(r=>r==null)||rows.Select(r=>r.ParameterId).Distinct().Count()!=rows.Count)throw new ArgumentException("迁移目标无效或重复");
                var allowed=MigrationFields(old,Catalog);var candidate=ContinuousSnapshot().Rules;candidate.RemoveAll(r=>r.Id==id);
                foreach(var row in rows)
                {
                    var target=allowed.SingleOrDefault(p=>p.Id==row.ParameterId);if(target==null)throw new ArgumentException("迁移目标已改变");
                    row.Id=Guid.NewGuid().ToString("N");row.Code=target.Reference;row.Enabled=false;row.MigrationPending=false;row.MigrationReason="";row.MigrationOriginalEnabled=false;row.ModifiedAt=BeijingTime.Now.ToString("o");
                    string error=CheckContinuousRule(row,true,true);if(error.Length>0)throw new ArgumentException(target.Reference+"："+error);
                    candidate.RemoveAll(r=>IsDefaultRule(r)&&r.ParameterId==target.Id&&(String.IsNullOrEmpty(r.SourceIP)||r.SourceIP==row.SourceIP));candidate.Add(row);
                }
                SaveContinuousRules(candidate,revision);
            }
        }
    }
    public sealed partial class Server
    {
        private bool HandleRuleMigration(HttpListenerContext ctx,Session session,string path,string method)
        {
            if(path=="/api/continuous-rules/migration"&&method=="GET")
            {Json(ctx.Response,monitor.RuleMigrationChoices(Query(ctx.Request,"id")),200);return true;}
            if(path=="/api/continuous-rules/migration-preview"&&method=="POST")
            {
                var body=Body(ctx.Request);object values;if(!body.TryGetValue("targets",out values))throw new ArgumentException("缺少迁移目标");
                lock(commandGate)Json(ctx.Response,monitor.PreviewRuleMigration(Text(body,"id"),JsonFile.Decode<string[]>(JsonFile.Encode(values))),200);return true;
            }
            if(path=="/api/continuous-rules/migration-apply"&&method=="POST")
            {
                var body=Body(ctx.Request,1024*1024);object values;if(!body.TryGetValue("rules",out values))throw new ArgumentException("缺少迁移规则");
                lock(commandGate)monitor.ApplyRuleMigration(Text(body,"id"),Text(body,"revision"),Text(body,"decoderRevision"),JsonFile.Decode<List<ContinuousRule>>(JsonFile.Encode(values)));
                Json(ctx.Response,new{ok=true},200);return true;
            }
            return false;
        }
    }
}
