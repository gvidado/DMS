using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;

namespace TelemetryMonitor
{
    public sealed partial class Server
    {
        private readonly object flightPreviewGate=new object();
        private readonly Dictionary<string,FlightSequencePreview> flightPreviews=new Dictionary<string,FlightSequencePreview>();

        private FlightSequencePreview FindFlightPreview(string token,Session session)
        {
            FlightSequencePreview preview;
            if(!flightPreviews.TryGetValue(token,out preview)||preview.Owner!=session.Token||
                (DateTime.UtcNow-preview.Updated).TotalMinutes>10)
                throw new ArgumentException("转换预览已过期，请重新读取飞行判读表");
            return preview;
        }

        private bool HandleFlightSequences(HttpListenerContext ctx,Session session,string path,string method)
        {
            if(!path.StartsWith("/api/sequence/flight/",StringComparison.Ordinal))return false;
            if(method!="POST")throw new ArgumentException("请选择飞行判读表转换操作");

            if(path=="/api/sequence/flight/preview")
            {
                string filename=Path.GetFileName(Query(ctx.Request,"filename"));
                if(!filename.EndsWith(".xlsx",StringComparison.OrdinalIgnoreCase))throw new ArgumentException("请选择 .xlsx 飞行判读表");
                if(filename.Length>180)throw new ArgumentException("文件名过长，请缩短文件名后读取");
                var sheets=WorkbookReader.ReadFlightSequences(BinaryBody(ctx.Request));
                var result=monitor.ConvertFlightSequences(filename,sheets);
                var preview=new FlightSequencePreview{Token=Auth.RandomToken(),Owner=session.Token,Filename=filename,Sheets=sheets};
                lock(flightPreviewGate)
                {
                    DateTime now=DateTime.UtcNow;
                    foreach(string expired in flightPreviews.Where(item=>(now-item.Value.Updated).TotalMinutes>10).Select(item=>item.Key).ToArray())
                        flightPreviews.Remove(expired);
                    if(flightPreviews.Count>=16)throw new InvalidOperationException("飞行判读表预览较多，请关闭其他预览后再试");
                    preview.Updated=now;
                    flightPreviews.Add(preview.Token,preview);
                }
                Json(ctx.Response,new{token=preview.Token,sheets=result},200);
                return true;
            }

            var body=Body(ctx.Request,8*1024*1024);
            string token=Text(body,"token");
            if(path=="/api/sequence/flight/cancel")
            {
                lock(flightPreviewGate)
                {
                    FlightSequencePreview preview;
                    if(flightPreviews.TryGetValue(token,out preview)&&preview.Owner==session.Token)flightPreviews.Remove(token);
                }
                Json(ctx.Response,new{ok=true},200);
                return true;
            }
            if(path=="/api/sequence/flight/recheck")
            {
                object edits;
                if(!body.TryGetValue("edits",out edits))throw new ArgumentException("缺少需要复检的行");
                var changes=JsonFile.Decode<List<FlightSequenceRow>>(JsonFile.Encode(edits));
                Monitor.FlightSheetResult result;
                lock(flightPreviewGate)
                {
                    var preview=FindFlightPreview(token,session);
                    int index=preview.Sheets.FindIndex(sheet=>sheet.Id==Text(body,"sheetId"));
                    if(index<0)throw new ArgumentException("预览中没有该工作表");
                    var updated=EditFlightSheet(preview.Sheets[index],changes);
                    result=monitor.ConvertFlightSequences(preview.Filename,new[]{updated})[0];
                    preview.Sheets[index]=updated;
                    preview.Updated=DateTime.UtcNow;
                }
                Json(ctx.Response,new{sheet=result},200);
                return true;
            }
            if(path=="/api/sequence/flight/export")
            {
                byte[] bytes;
                string filename;
                lock(flightPreviewGate)
                {
                    var preview=FindFlightPreview(token,session);
                    var result=monitor.ConvertFlightSequences(preview.Filename,preview.Sheets);
                    var invalid=result.FirstOrDefault(sheet=>sheet.Errors.Count>0);
                    if(invalid!=null)throw new ArgumentException("工作表“"+invalid.Name+"”仍有错误。"+invalid.Errors[0]+"。请修正并重新检测。");
                    bytes=WorkbookReader.ExportConvertedSequences(result.Select(sheet=>sheet.Definition).ToList());
                    filename=Path.GetFileNameWithoutExtension(preview.Filename)+"_序列判读.xlsx";
                    preview.Updated=DateTime.UtcNow;
                }
                ctx.Response.ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
                ctx.Response.Headers["Content-Disposition"]="attachment; filename=flight-sequence.xlsx; filename*=UTF-8''"+Uri.EscapeDataString(filename);
                ctx.Response.ContentLength64=bytes.Length;
                ctx.Response.OutputStream.Write(bytes,0,bytes.Length);
                return true;
            }
            Json(ctx.Response,new{error="飞行判读表转换接口不存在"},404);
            return true;
        }

        private static FlightSequenceSheet EditFlightSheet(FlightSequenceSheet sheet,List<FlightSequenceRow> edits)
        {
            if(edits==null||edits.Count>sheet.Rows.Count||edits.Any(row=>row==null)||edits.Select(row=>row.Row).Distinct().Count()!=edits.Count)
                throw new ArgumentException("修改行号无效");
            var changes=edits.ToDictionary(row=>row.Row);
            var updated=new FlightSequenceSheet{Id=sheet.Id,Name=sheet.Name};
            foreach(var row in sheet.Rows)
            {
                FlightSequenceRow edit;
                if(!changes.TryGetValue(row.Row,out edit)){updated.Rows.Add(row);continue;}
                changes.Remove(row.Row);
                updated.Rows.Add(new FlightSequenceRow{Row=row.Row,Phase=row.Phase,Action=row.Action,
                    Parameter=FlightEditText(edit.Parameter),Reference=FlightEditText(edit.Reference),
                    Range=FlightEditText(edit.Range),Keyword=FlightEditText(edit.Keyword)});
            }
            if(changes.Count>0)throw new ArgumentException("修改中含有工作表不存在的行");
            return updated;
        }

        private static string FlightEditText(string value)
        {
            value=(value??"").Trim();
            if(value.Length>128)throw new ArgumentException("参数代号、参考值、范围值和关键字最多128个字符");
            return value;
        }
    }
}
