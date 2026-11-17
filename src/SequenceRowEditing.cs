using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace TelemetryMonitor
{
    internal static class SequenceRowEditing
    {
        internal static void Update(SequenceDefinition definition,List<SequenceRow> rows,string requestedNext)
        {
            if(rows==null || rows.Count>5000 || rows.Any(r=>r==null || r.Row<1 || r.Row==Int32.MaxValue))throw new ArgumentException("预览行无效：最多5000行，每行须有有效的独立行号");
            if(rows.Select(r=>r.Row).Distinct().Count()!=rows.Count)throw new ArgumentException("预览行号重复，请重新载入后增删行");
            // Keep original row numbers as stable step identities. Never recycle a deleted row:
            // live configuration adoption and immutable historical results both key steps by Row.
            long watermark=Math.Max((long)definition.NextRowNumber,definition.Rows.Select(r=>(long)r.Row+1).DefaultIfEmpty(1).Max());
            int supplied=0;if(!String.IsNullOrWhiteSpace(requestedNext) && (!Int32.TryParse(requestedNext,NumberStyles.None,CultureInfo.InvariantCulture,out supplied)||supplied<1))throw new ArgumentException("新增行编号状态无效，请重新载入");
            var previous=definition.Rows.GroupBy(r=>r.Row).ToDictionary(g=>g.Key,g=>g.First());var updated=new List<SequenceRow>();
            var fields=new[]{"Keyword","Description","Command","Condition","Mark","Expected","Timeout"};
            foreach(var next in rows)
            {
                SequenceRow original;
                if(!previous.TryGetValue(next.Row,out original))
                {
                    if(next.Row<watermark)throw new ArgumentException("新增行不能使用已删除或原表的行号："+next.Row+"；请重新载入后添加");
                    original=new SequenceRow{Row=next.Row,Added=true};
                }
                foreach(string field in fields)
                {
                    var member=typeof(SequenceRow).GetField(field);string value=(string)member.GetValue(next)??"";
                    if((string)member.GetValue(original)!=value && original.CellErrors!=null)original.CellErrors.Remove(field);
                    member.SetValue(original,value);
                }
                if(original.CommandNote.Length>0 && original.Description.EndsWith(original.CommandNote,StringComparison.Ordinal))original.Description=original.Description.Substring(0,original.Description.Length-original.CommandNote.Length);
                original.CommandNote="";updated.Add(original);
            }
            watermark=Math.Max(watermark,Math.Max((long)supplied,updated.Select(r=>(long)r.Row+1).DefaultIfEmpty(1).Max()));
            if(watermark>Int32.MaxValue)throw new ArgumentException("新增行编号超出范围，请重新导入序列");
            definition.Rows=updated;definition.NextRowNumber=(int)watermark;
        }
    }
}
