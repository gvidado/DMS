using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;

namespace TelemetryMonitor
{
    public static class ParameterEditing
    {
        private static string EnumKey(string value)
        {
            string text=(value??"").Trim();bool negative=text.StartsWith("-",StringComparison.Ordinal);string digits=negative?text.Substring(1):text;decimal number;
            if(digits.StartsWith("0x",StringComparison.OrdinalIgnoreCase)){ulong hex;if(!UInt64.TryParse(digits.Substring(2),NumberStyles.AllowHexSpecifier,CultureInfo.InvariantCulture,out hex))throw new ArgumentException("枚举原码无效："+value);number=negative?-(decimal)hex:(decimal)hex;}
            else if(!Decimal.TryParse(text,NumberStyles.AllowLeadingSign,CultureInfo.InvariantCulture,out number))throw new ArgumentException("枚举原码应为十进制整数或0x十六进制："+value);
            if(number<Int64.MinValue || number>UInt64.MaxValue)throw new ArgumentException("枚举原码超出64位范围");return number.ToString(CultureInfo.InvariantCulture);
        }
        public static Parameter Replace(DecoderConfiguration candidate,string id,Parameter edit)
        {
            var original=candidate.Catalog.Parameters.FirstOrDefault(p=>p.Id==id);if(original==null)throw new ArgumentException("参数已不存在，请刷新参数表");
            if(edit==null || edit.Id!=original.Id || edit.Code!=original.Code || edit.Group!=original.Group)throw new ArgumentException("参数标识、代号和所属参数表须保持不变，以保留已有判读绑定");
            var next=JsonFile.Decode<Parameter>(JsonFile.Encode(original));next.Name=(edit.Name??"").Trim();next.Unit=(edit.Unit??"").Trim();next.Type=edit.Type;next.Offset=edit.Offset;next.Length=edit.Length;next.BitStart=edit.BitStart;next.BitCount=edit.BitCount;next.Scale=edit.Scale;next.Bias=edit.Bias;next.Precision=edit.Precision;next.Format=edit.Format??"";next.Note=edit.Note??"";next.Supported=edit.Supported;next.Diagnostic=next.Supported?"":(edit.Diagnostic??"").Trim();
            if(next.Name.Length==0 || next.Name.Length>240 || next.Unit.Length>100 || next.Note.Length>100000 || next.Diagnostic.Length>4000)throw new ArgumentException("参数名称、单位或描述长度无效");
            if(!new[]{"十进制","十六进制"}.Contains(next.Format))throw new ArgumentException("显示格式应为十进制或十六进制");
            if(next.BitStart<0 && next.BitCount!=0)throw new ArgumentException("整字节参数的位宽应为0");
            if(next.Offset<0 || next.Length<1 || (long)next.Offset+next.Length>65507)throw new ArgumentException("参数位置超出UDP用户数据域可用范围");
            if(next.Supported && next.BitStart>=0 && new[]{"float32","float64","ascii","bcd"}.Contains(next.Type))throw new ArgumentException("位字段请使用整数类型，浮点、ASCII或BCD应使用整字节模式");
            next.Enums=new Dictionary<string,string>();foreach(var pair in edit.Enums??new Dictionary<string,string>()){string key=EnumKey(pair.Key);if(next.Enums.ContainsKey(key))throw new ArgumentException("枚举原码重复："+key);if(String.IsNullOrWhiteSpace(pair.Value))throw new ArgumentException("枚举说明不能为空");next.Enums.Add(key,pair.Value);}
            next.EnumRanges=edit.EnumRanges??new List<EnumRange>();if(next.EnumRanges.Any(r=>r==null || String.IsNullOrWhiteSpace(r.Text)))throw new ArgumentException("区间注释说明不能为空");
            next.CrossByte=next.BitStart>=0 && next.Length>1;int first=next.BitStart>=0?next.BitStart:0;int last=next.BitStart>=0?next.BitStart+next.BitCount-1:next.Length*8-1;
            next.Position=next.BitStart>=0?"Byte"+next.Offset+"_B"+first+"~Byte"+(next.Offset+last/8)+"_B"+(last%8):"Byte"+next.Offset+"~Byte"+(next.Offset+next.Length-1);
            candidate.Catalog.Parameters[candidate.Catalog.Parameters.IndexOf(original)]=next;
            foreach(var sheet in candidate.Catalog.Sheets.Where(s=>s.Name==next.Group)){var fields=candidate.Catalog.Parameters.Where(p=>p.Group==sheet.Name).ToArray();sheet.Parameters=fields.Length;sheet.Supported=fields.Count(p=>p.Supported);}
            if(next.Supported)candidate.Catalog.Issues.RemoveAll(issue=>issue.Sheet==original.Group && issue.Row==original.SourceRow && issue.Code==original.Code);
            candidate.Validate();return next;
        }
    }
    public static partial class WorkbookReader
    {
        public static object ParseEditedNote(string note)
        {if((note??"").Length>100000)throw new ArgumentException("物理意义文字过长");var parameter=new Parameter{Note=note??"",Code="",Group=""};var catalog=new TelemetryCatalog();ReadEnums(catalog,parameter);return new{enums=parameter.Enums,ranges=parameter.EnumRanges,issues=catalog.Issues};}
    }
    public sealed partial class Server
    {
        private bool HandleParameterEditing(HttpListenerContext ctx,Session session)
        {
            string path=ctx.Request.Url.AbsolutePath,method=ctx.Request.HttpMethod;
            if(path=="/api/decoders/parameter" && method=="GET")
            {
                Json(ctx.Response,monitor.ReadParameterDefinition(Query(ctx.Request,"id")),200);return true;
            }
            if(path=="/api/decoders/parameter/note" && method=="POST")
            {Json(ctx.Response,WorkbookReader.ParseEditedNote(Text(Body(ctx.Request,1024*1024),"note")),200);return true;}
            if(path=="/api/decoders/parameter" && method=="POST")
            {
                var body=Body(ctx.Request,1024*1024);object value;if(!body.TryGetValue("parameter",out value))throw new ArgumentException("缺少参数定义");var raw=value as Dictionary<string,object>;if(raw==null || new[]{"Offset","Length","BitStart","BitCount","Scale","Bias","Precision","Supported"}.Any(key=>!raw.ContainsKey(key) || raw[key]==null))throw new ArgumentException("缺少必要解码字段或字段为空");
                if(!(raw["Supported"] is bool))throw new ArgumentException("定义完整状态应为布尔值");foreach(string key in new[]{"Offset","Length","BitStart","BitCount","Scale","Bias","Precision"}){object number=raw[key];if(!(number is int || number is long || number is double || number is decimal))throw new ArgumentException(key+"应为数值");if(key!="Scale" && key!="Bias"){double integer=Convert.ToDouble(number,CultureInfo.InvariantCulture);if(integer<Int32.MinValue || integer>Int32.MaxValue || Math.Truncate(integer)!=integer)throw new ArgumentException(key+"应为有效整数");}}
                object ranges;if(raw.TryGetValue("EnumRanges",out ranges) && ranges!=null){if(ranges is string || !(ranges is System.Collections.IEnumerable))throw new ArgumentException("区间注释格式无效");foreach(object range in (System.Collections.IEnumerable)ranges){var entry=range as Dictionary<string,object>;if(entry==null || !entry.ContainsKey("Min") || entry["Min"]==null || !entry.ContainsKey("Max") || entry["Max"]==null)throw new ArgumentException("区间注释需填写上下限");}}
                var edit=JsonFile.Decode<Parameter>(JsonFile.Encode(value));
                lock(commandGate)
                {
                    var candidate=monitor.DecoderSnapshot();if(Text(body,"revision")!=candidate.Revision)throw new InvalidOperationException("解码配置已被其他操作修改，请刷新后重新编辑");var updated=ParameterEditing.Replace(candidate,Text(body,"id"),edit);monitor.ApplyDecoder(candidate,Text(body,"revision"));monitor.Note("info","单项参数定义已更新："+updated.Group+" / "+updated.Reference+"；操作人："+auth.User(session).DisplayName);
                }
                Json(ctx.Response,new{ok=true,revision=monitor.DecoderRevision},200);return true;
            }
            return false;
        }
    }
}
