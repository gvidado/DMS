using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace TelemetryMonitor
{
    // References are derived metadata. Excel codes and stable parameter IDs retain their identity.
    public static class TelemetryReferences
    {
        public static string Name(string code)
        {
            string value = (code ?? "").Trim();
            if (value.Length == 0) return "";
            return HasPrefix(value) ? value : "TM." + value;
        }
        public static bool HasPrefix(string value)
        {return value!=null && (value.StartsWith("TM.",StringComparison.Ordinal)||value.StartsWith("TME.",StringComparison.Ordinal)||value.StartsWith("TMB.",StringComparison.Ordinal));}
        public static string CodeOf(string value)
        {value=(value??"").Trim();return HasPrefix(value)?value.Substring(value.IndexOf('.')+1):value;}
        public static string Name(string code,string space)
        {return String.IsNullOrEmpty(space)?Name(code):space+"."+CodeOf(code);}
        public static string NamespaceFor(int source)
        {return source<0?"":source==0x0202||source==0x0422?"TMB":"TME";}

        public static List<ImportIssue> Issues(IEnumerable<Parameter> parameters)
        {
            var issues = new List<ImportIssue>();
            if (parameters == null) return issues;
            var fields = parameters.Where(p => p != null).ToArray();
            foreach (Parameter p in fields)
                if (!Regex.IsMatch(p.Reference, @"^(?:TM|TME|TMB)\.[\p{L}_][\p{L}\p{Nd}_]*$"))
                    issues.Add(new ImportIssue { Level = "warning", Sheet = p.Group, Row = p.SourceRow, Code = p.Code,
                        Message = "参数代号无法作为TM引用：请使用字母或下划线开头，后续使用字母、数字或下划线。" });
            foreach (var group in fields.GroupBy(p => p.Reference, StringComparer.Ordinal).Where(g => g.Count() > 1))
                foreach (Parameter p in group)
                    issues.Add(new ImportIssue { Level = "warning", Sheet = p.Group, Row = p.SourceRow, Code = p.Code,
                        Message = "参数引用" + group.Key + "重复（" + group.Count() + "项）。请调整参数代号，避免后续判读引用歧义。" });
            return issues;
        }
    }
}
