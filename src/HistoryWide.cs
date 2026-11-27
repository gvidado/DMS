using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace TelemetryMonitor
{
    internal sealed class HistoryWideColumn
    {
        public string key,code,name,unit,packet,packetId,label;
    }

    internal sealed partial class HistoryStorage
    {
        private HistoryWideColumn[] HistoryWideColumns(HistoryPostgresClient database,HistoryQuery query)
        {
            var args=new List<string>();
            var where=new List<string>();
            HistoryWhere(where,HistorySelected(args,query.Selection("namespace"),"p.namespace"));
            HistoryWhere(where,HistorySelected(args,query.Selection("packet"),"p.packet_id"));
            var selected=query.Selection("parameter");
            if(selected.Values.Length>0)
            {
                if(selected.IsList)HistoryWhere(where,HistorySelected(args,selected,"p.reference"));
                else{args.Add(selected.Values[0]);where.Add("position(lower($"+args.Count+") in lower(p.reference||' '||p.name))>0");}
            }
            string sql=@"SELECT runtime_id,reference,string_agg(DISTINCT name,' / ' ORDER BY name) AS name,
string_agg(DISTINCT unit,' / ' ORDER BY unit) AS unit,
string_agg(DISTINCT packet_name,' / ' ORDER BY packet_name) AS packet,min(packet_id) AS packet_id
FROM td_history.parameter_definition p WHERE "+(where.Count==0?"true":String.Join(" AND ",where))+@"
GROUP BY runtime_id,reference ORDER BY reference,runtime_id";
            var columns=database.Query(sql,args.ToArray()).Select(row=>new HistoryWideColumn{
                key=row["runtime_id"]+""+row["reference"],code=row["reference"],
                name=row["name"],unit=row["unit"],packet=row["packet"],packetId=row["packet_id"]}).ToArray();
            foreach(var group in columns.GroupBy(column=>column.code))
            {
                bool duplicate=group.Skip(1).Any();
                foreach(var column in group)column.label=column.code+" "+column.name+(duplicate?" ["+column.packet+"]":"");
            }
            return columns;
        }

        internal void ExportHistoryWorkbook(HistoryQuery query,Stream destination)
        {
            query.Type="wide";query.Cursor="";query.Validate();
            if(!querySlots.WaitOne(0))throw new InvalidOperationException("历史查询较多，请稍后导出");
            HistoryStorageConfiguration settings;lock(gate)settings=configuration;
            if(!settings.DatabaseEnabled){querySlots.Release();throw new InvalidOperationException("请先启用历史数据库");}
            try
            {
                using(var database=new HistoryPostgresClient(root,settings))
                {
                    database.Query("BEGIN ISOLATION LEVEL REPEATABLE READ READ ONLY");
                    try
                    {
                        var columns=HistoryWideColumns(database,query);
                        using(var workbook=new HistoryWorkbookWriter(destination))
                        {
                            int blocks=Math.Max(1,(columns.Length+HistoryWorkbookWriter.ParametersPerSheet-1)/HistoryWorkbookWriter.ParametersPerSheet);
                            for(int block=0;block<blocks;block++)
                            {
                                workbook.StartColumns(columns.Skip(block*HistoryWorkbookWriter.ParametersPerSheet).Take(HistoryWorkbookWriter.ParametersPerSheet).ToArray());
                                query.Size=500;query.Cursor="";
                                while(true)
                                {
                                    var page=(Dictionary<string,object>)QueryHistoryCore(database,query,false);
                                    foreach(var row in (Dictionary<string,object>[])page["rows"])workbook.Add(row);
                                    if(!Convert.ToBoolean(page["hasMore"]))break;
                                    query.Cursor=Convert.ToString(page["cursor"]);
                                }
                            }
                        }
                        database.Query("COMMIT");
                    }
                    catch{try{database.Query("ROLLBACK");}catch{}throw;}
                }
            }
            finally{querySlots.Release();}
        }
    }
}
