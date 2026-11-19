using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace TelemetryMonitor
{
    internal sealed class FlightSequencePreview
    {
        public string Token, Owner, Filename;
        public DateTime Updated;
        public List<FlightSequenceSheet> Sheets;
    }

    public sealed partial class Monitor
    {
        private static readonly Regex FlightComparator=new Regex(
            @"^(大于或等于|小于或等于|大于等于|小于等于|大于|小于|≥|≤|>=|<=|>|<)\s*(.+)$",RegexOptions.CultureInvariant);
        private static readonly Regex FlightHex=new Regex(@"^-?0[xX][0-9a-fA-F]+$",RegexOptions.CultureInvariant);

        internal sealed class FlightRowResult
        {
            public FlightSequenceRow Input;
            public string ParameterName, Description, Condition;
            public string[] Candidates;
            public Dictionary<string,string> Errors;
        }

        internal sealed class FlightSheetResult
        {
            internal SequenceDefinition Definition;
            public string Id, Name;
            public List<FlightRowResult> Rows=new List<FlightRowResult>();
            public List<string> Errors, Warnings;
        }

        internal List<FlightSheetResult> ConvertFlightSequences(string filename,IEnumerable<FlightSequenceSheet> sheets)
        {
            var catalog=Catalog;
            var references=catalog.ToLookup(p=>p.Reference,StringComparer.OrdinalIgnoreCase);
            var codes=catalog.ToLookup(p=>TelemetryReferences.CodeOf(p.Code),StringComparer.OrdinalIgnoreCase);
            var results=new List<FlightSheetResult>();
            foreach(var sheet in sheets)
            {
                var definition=new SequenceDefinition{Id=sheet.Id,Filename=filename,Sheet=sheet.Name,
                    Name=System.IO.Path.GetFileNameWithoutExtension(filename)+" / "+sheet.Name,Mode="judge"};
                var result=new FlightSheetResult{Id=sheet.Id,Name=sheet.Name,Definition=definition};
                foreach(var row in sheet.Rows)
                {
                    var converted=ConvertFlightRow(row,references,codes);
                    result.Rows.Add(converted);
                    definition.Rows.Add(new SequenceRow{Row=row.Row,Keyword=row.Keyword,Description=converted.Description,
                        Condition=converted.Condition,CellErrors=converted.Errors});
                }
                lock(Gate)
                    CompileSequence(definition,sequenceConfiguration.DefaultTimeoutSeconds,out result.Errors,out result.Warnings);
                results.Add(result);
            }
            return results;
        }

        private static FlightRowResult ConvertFlightRow(FlightSequenceRow row,ILookup<string,Parameter> references,
            ILookup<string,Parameter> codes)
        {
            var result=new FlightRowResult{Input=row,Condition="",ParameterName="",Candidates=new string[0],
                Errors=new Dictionary<string,string>()};
            string label=String.Join("：",new[]{row.Phase,row.Action}.Where(part=>part.Length>0));
            result.Description=label;
            if(row.Parameter==""&&row.Reference==""&&row.Range=="")return result;

            string reference=row.Parameter.Normalize(NormalizationForm.FormKC).Replace('。','.');
            if(reference.EndsWith(".value",StringComparison.OrdinalIgnoreCase))
                reference=reference.Substring(0,reference.Length-6);
            var matches=(reference.Contains(".")?references[reference]:codes[reference]).ToArray();
            if(matches.Length==1&&matches[0].Supported)
            {
                var parameter=matches[0];
                result.ParameterName=parameter.Name;
                result.Description=label+(label.Length==0?parameter.Name:"（"+parameter.Name+"）");
                reference=parameter.Reference;
            }
            else
            {
                result.Candidates=matches.Select(p=>p.Reference).Distinct().ToArray();
                result.Description=label+(row.Parameter.Length==0?"":"（"+row.Parameter+"）");
                result.Errors["参数代号"]=matches.Length>1?"参数代号匹配多项，请选用唯一的完整引用。":
                    matches.Length==1?"参数解码定义不完整，请选择其他参数或修正解码配置。":
                    row.Parameter.Length==0?"请填写判读参数代号。":"参数未找到，请改选解码配置中的参数。";
            }

            string referenceText=row.Reference.Normalize(NormalizationForm.FormKC).Trim();
            var comparison=FlightComparator.Match(referenceText);
            decimal number,range;
            string value,rangeExpression;
            if(comparison.Success)
            {
                if(!FlightNumber(comparison.Groups[2].Value,out number,out value))
                    result.Errors["参考值"]="比较符后请填写有效的数值。";
                if(result.Errors.Count==0)
                    result.Condition=reference+" "+FlightOperator(comparison.Groups[1].Value)+" "+value;
                return result;
            }

            if(!FlightNumber(referenceText,out number,out value))
                result.Errors["参考值"]="请填写数值或比较条件，例如 9.8、0x020202、>10。";
            if(!FlightNumber(row.Range,out range,out rangeExpression)||range<0)
                result.Errors["范围值"]=row.Range.Length==0?"参考值为具体数值时，范围值不能为空。":"范围值须为 0 或正数。";
            if(result.Errors.Count==0)
                result.Condition=range==0?reference+" == "+value:
                    "abs("+reference+" - "+(number<0?"("+value+")":value)+") < "+rangeExpression;
            return result;
        }

        private static string FlightOperator(string value)
        {
            switch(value)
            {
                case "大于":return ">";
                case "小于":return "<";
                case "大于或等于":case "大于等于":case "≥":return ">=";
                case "小于或等于":case "小于等于":case "≤":return "<=";
                default:return value;
            }
        }

        private static bool FlightNumber(string text,out decimal value,out string expression)
        {
            text=text.Normalize(NormalizationForm.FormKC).Trim().Replace('−','-');
            expression="";
            value=0;
            if(FlightHex.IsMatch(text))
            {
                bool negative=text[0]=='-';
                string digits=text.Substring(negative?3:2);
                ulong raw;
                if(!UInt64.TryParse(digits,NumberStyles.AllowHexSpecifier,CultureInfo.InvariantCulture,out raw))return false;
                value=negative?-(decimal)raw:(decimal)raw;
                expression=(negative?"-":"")+"0x"+digits.ToUpperInvariant();
                return true;
            }
            if(!Decimal.TryParse(text,NumberStyles.Float,CultureInfo.InvariantCulture,out value))return false;
            expression=value.ToString("G29",CultureInfo.InvariantCulture);
            return true;
        }
    }
}
