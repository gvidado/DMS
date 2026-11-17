using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;

namespace TelemetryMonitor
{
    public sealed partial class Server
    {
        private readonly Dictionary<string, ImportPreview> previews = new Dictionary<string, ImportPreview>();
        private static string Query(HttpListenerRequest request, string key)
        {
            // HttpListener.QueryString can use the server ANSI codepage; browsers encode filenames as UTF-8.
            string raw = request.RawUrl ?? ""; int mark = raw.IndexOf('?');
            if (mark < 0) return "";
            foreach (string part in raw.Substring(mark + 1).Split('&'))
            {
                int equal = part.IndexOf('='); if (equal < 0) continue;
                if (Uri.UnescapeDataString(part.Substring(0, equal).Replace("+", " ")) == key) return Uri.UnescapeDataString(part.Substring(equal + 1).Replace("+", " "));
            }
            return "";
        }
        private static byte[] BinaryBody(HttpListenerRequest request)
        {
            const int limit = 8 * 1024 * 1024;
            if (request.ContentLength64 > limit) throw new ArgumentException("导入文件最大8MB");
            using (var memory = new MemoryStream())
            {
                byte[] buffer = new byte[8192]; int read;
                while ((read = request.InputStream.Read(buffer, 0, buffer.Length)) > 0)
                { if (memory.Length + read > limit) throw new ArgumentException("导入文件最大8MB"); memory.Write(buffer, 0, read); }
                return memory.ToArray();
            }
        }
        private ImportPreview Preview(string token, Session session)
        {
            ImportPreview preview;
            if (String.IsNullOrEmpty(token) || !previews.TryGetValue(token, out preview) || preview.Username != session.Username || (DateTime.UtcNow - preview.Created).TotalMinutes > 10) throw new ArgumentException("导入预览已过期或不属于当前账户，请重新选择文件");
            return preview;
        }
        private static void Download(HttpListenerResponse response, object value)
        {
            response.Headers["Content-Disposition"] = "attachment; filename=telemetry-decoder.json";
            Json(response, value, 200);
        }
        private static DecoderConfiguration PreviewConfiguration(ImportPreview preview, string mode)
        {
            if (preview.Review != null && (preview.Review.ErrorCount > 0 || preview.Review.Rows.All(r => r.Discarded))) throw new ArgumentException("请先修正或舍弃预览中的错误参数，且至少保留一个参数，再导入或导出配置");
            if (String.IsNullOrEmpty(mode) || mode == "replace")
            { if (!preview.CanReplace) throw new ArgumentException(String.Join("；", preview.ReviewErrors)); return preview.Configuration; }
            if (mode == "replace-matching")
            {
                if (preview.MatchingConfiguration == null || preview.Matching == null || !preview.Matching.CanApply)
                    throw new ArgumentException(preview.Matching == null ? "仅替换同名表请选择遥测Excel" : String.Join("；", preview.Matching.Errors));
                return preview.MatchingConfiguration;
            }
            if (mode != "add") throw new ArgumentException("请选择替换、添加或仅替换同名表");
            if (preview.AddConfiguration == null || preview.Addition == null || !preview.Addition.CanApply)
                throw new ArgumentException(preview.Addition == null ? "该预览不支持添加，请读取遥测Excel" :
                    String.Join("；", preview.Addition.Errors.Concat(preview.Addition.Conflicts.Take(5))));
            return preview.AddConfiguration;
        }
        private bool HandleDecoder(HttpListenerContext ctx, Session session)
        {
            string path = ctx.Request.Url.AbsolutePath, method = ctx.Request.HttpMethod;
            if (!path.StartsWith("/api/decoders", StringComparison.Ordinal)) return false;
            if(HandleParameterEditing(ctx,session))return true;
            if(HandleTelemetryImportReview(ctx,session))return true;
            if (path == "/api/decoders" && method == "GET")
            {
                object snapshot;
                lock (monitor.Gate) { var config = monitor.DecoderSnapshot(); snapshot = new { configuration = config, revision = config.Revision, native = monitor.NativeDecoder }; }
                Json(ctx.Response, snapshot, 200); return true;
            }
            if (path == "/api/decoders/export" && method == "GET")
            { Download(ctx.Response, monitor.DecoderSnapshot()); return true; }
            if (path == "/api/decoders/spp/batch/preview" && method == "POST")
            {
                DateTime requestedAt = DateTime.UtcNow;
                string filename = Path.GetFileName(Query(ctx.Request, "filename"));
                if (!String.Equals(Path.GetExtension(filename), ".xlsx", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("请选择.xlsx格式的SPP包定义表");
                DecoderConfiguration candidate = monitor.DecoderSnapshot();
                SppWorkbookBatch batch = WorkbookReader.ReadAllSpp(BinaryBody(ctx.Request), filename);
                if (batch.Errors.Count > 0)
                { Json(ctx.Response, new { error = "SPP工作簿有" + batch.Errors.Count + "张表读取失败，请修正后重新读取；本次未导入任何包", errors = batch.Errors }, 400); return true; }
                var rows = new List<object>();
                foreach (SppWorkbookPreview sheet in batch.Packets)
                {
                    PacketDefinition existing = candidate.Packets.FirstOrDefault(packet => packet.SppSourceFile == filename && packet.SppSourceSheet == sheet.Sheet);
                    PacketDefinition p = existing == null ? new PacketDefinition() : JsonFile.Decode<PacketDefinition>(JsonFile.Encode(existing));
                    if (existing == null)
                    {
                        p.Id = "spp-" + Guid.NewGuid().ToString("N"); p.Name = sheet.Sheet;
                        candidate.Packets.Add(p);
                    }
                    else candidate.Packets[candidate.Packets.IndexOf(existing)] = p;
                    p.SppSourceFile = filename; p.SppSourceSheet = sheet.Sheet;
                    if(sheet.Fields["OuterSourceId"]>=0||sheet.OuterSourceUnrestricted)p.OuterSourceId=sheet.Fields["OuterSourceId"];
                    p.Version = sheet.Fields["Version"]; p.PacketType = sheet.Fields["PacketType"]; p.SecondaryHeader = sheet.Fields["SecondaryHeader"]; p.Apid = sheet.Fields["Apid"];
                    p.ExpectedLengthField = sheet.Fields["ExpectedLengthField"]; p.ServiceType = sheet.Fields["ServiceType"]; p.ServiceSubtype = sheet.Fields["ServiceSubtype"];
                    p.SourceAddress = sheet.Fields["SourceAddress"]; p.DestinationAddress = sheet.Fields["DestinationAddress"];
                    if (existing == null || existing.SecondaryHeader != p.SecondaryHeader)
                    { p.PayloadOffset = p.SecondaryHeader == 1 ? 16 : 6; }
                    p.Enabled = false; p.MappingConfirmed = false; p.MonitorRaw = false;
                    rows.Add(new { sheet = sheet, packetId = p.Id, action = existing == null ? "新增" : "更新", name = p.Name, groups = p.Groups });
                }
                candidate.Validate();
                var warnings = new List<string>();
                foreach(var row in batch.Packets.Where(p=>p.Fields["OuterSourceId"]<0))warnings.Add(row.Sheet+(row.OuterSourceUnrestricted?"：包来源为/，仅可用于不参与解码的原包监测，勾选后只按APID匹配。":"：Excel未提供数据源；新包需在编辑中补填，已有包沿用保存的来源标识。"));
                foreach (var group in candidate.Packets.GroupBy(p => p.OuterSourceId+"/"+PacketCodec.Identifier(p) + "/" + p.SourceAddress + "/" + p.ServiceType + "/" + p.ServiceSubtype).Where(g => g.Count() > 1))
                {
                    var packets = group.ToArray();
                    bool overlap = packets.Any(a => packets.Any(b => a.Id != b.Id && (String.IsNullOrEmpty(a.SourceIP) || String.IsNullOrEmpty(b.SourceIP) || a.SourceIP == b.SourceIP)));
                    if (overlap) warnings.Add("APID " + packets[0].Apid + " 的SPP匹配字段组合重复（包标识/源地址/业务类型/业务子类型=" + group.Key + "）：" + String.Join("、", packets.Select(p => p.Name)) + "。启用前请区分匹配字段或来源IP，或停用重复定义。");
                }
                lock (commandGate)
                {
                    foreach (string key in previews.Where(p => (p.Value.Username == session.Username && p.Value.Created <= requestedAt) || (DateTime.UtcNow - p.Value.Created).TotalMinutes > 10).Select(p => p.Key).ToList()) previews.Remove(key);
                    if (previews.Count >= 16) throw new InvalidOperationException("当前导入预览较多，请稍后重试");
                    var preview = new ImportPreview { Token = Auth.RandomToken(), Username = session.Username, Created = DateTime.UtcNow, Revision = candidate.Revision, Configuration = candidate };
                    previews[preview.Token] = preview;
                    Json(ctx.Response, new { token = preview.Token, revision = preview.Revision, sourceFile = filename, rows = rows, warnings = warnings }, 200);
                }
                return true;
            }
            if (path == "/api/decoders/spp/preview" && method == "POST")
            {
                string filename = Path.GetFileName(Query(ctx.Request, "filename"));
                if (!String.Equals(Path.GetExtension(filename), ".xlsx", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("请选择.xlsx格式的SPP包定义表");
                Json(ctx.Response, WorkbookReader.ReadSpp(BinaryBody(ctx.Request), filename, Query(ctx.Request, "sheet")), 200); return true;
            }
            if (path == "/api/decoders/preview" && method == "POST")
            {
                DateTime requestedAt = DateTime.UtcNow;
                string filename = Path.GetFileName(Query(ctx.Request, "filename")), extension = Path.GetExtension(filename).ToLowerInvariant();
                byte[] bytes = BinaryBody(ctx.Request); DecoderConfiguration candidate, addition = null, matching = null;
                CatalogAddition additionInfo;
                CatalogMatchingReplacement matchingInfo = new CatalogMatchingReplacement();
                DecoderConfiguration current = monitor.DecoderSnapshot();
                TelemetryImportReview review = null;
                if (extension == ".xlsx")
                {
                    review = WorkbookReader.ReadEditable(bytes, filename);
                    candidate = null; additionInfo = null;
                }
                else if (extension == ".json")
                {
                    candidate = JsonFile.Decode<DecoderConfiguration>(Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF'));
                    additionInfo = new CatalogAddition();
                    additionInfo.Errors.Add("JSON为完整配置，只支持替换；添加参数表请选择遥测.xlsx文件");
                    matchingInfo.Errors.Add("JSON为完整配置，只支持替换；仅替换同名表请选择遥测.xlsx文件");
                }
                else throw new ArgumentException("请选择.xlsx遥测表或本软件生成的.json解码配置");
                if (review == null) { if (candidate == null) throw new ArgumentException("空解码配置"); candidate.Validate(); }
                lock (commandGate)
                {
                    foreach (string expired in previews.Where(p => (DateTime.UtcNow - p.Value.Created).TotalMinutes > 10).Select(p => p.Key).ToList()) previews.Remove(expired);
                    // A cancelled, slow upload must not invalidate a newer preview that finished first.
                    foreach (string previous in previews.Where(p => p.Value.Username == session.Username && p.Value.Created <= requestedAt).Select(p => p.Key).ToList()) previews.Remove(previous);
                    if (previews.Count >= 16) throw new InvalidOperationException("当前导入预览较多，请稍后重试");
                    var preview = new ImportPreview { Token = Auth.RandomToken(), Username = session.Username, Created = DateTime.UtcNow, Revision = current.Revision,
                        Configuration = candidate, AddConfiguration = addition, Addition = additionInfo, MatchingConfiguration = matching, Matching = matchingInfo, SourceType = extension.TrimStart('.'), Review = review, BaseConfiguration = current };
                    if (review != null) RebuildImportPreview(preview);
                    previews[preview.Token] = preview;
                    Json(ctx.Response, ImportPreviewResponse(preview), 200);
                }
                return true;
            }
            if (path == "/api/decoders/preview/export" && method == "GET")
            { lock (commandGate) Download(ctx.Response, PreviewConfiguration(Preview(Query(ctx.Request, "token"), session), Query(ctx.Request, "mode"))); return true; }
            if (path == "/api/decoders/preview/cancel" && method == "POST")
            {
                var body = Body(ctx.Request); string token = Text(body, "token");
                lock (commandGate)
                {
                    ImportPreview preview;
                    if (!String.IsNullOrEmpty(token) && previews.TryGetValue(token, out preview) && preview.Username == session.Username) previews.Remove(token);
                }
                Json(ctx.Response, new { ok = true }, 200); return true;
            }
            if (path == "/api/decoders/apply" && method == "POST")
            {
                var body = Body(ctx.Request);
                lock (commandGate)
                {
                    ImportPreview preview = Preview(Text(body, "token"), session);
                    if (Text(body, "revision") != preview.Revision) throw new InvalidOperationException("导入预览版本不匹配，请重新预览");
                    monitor.ApplyDecoder(PreviewConfiguration(preview, Text(body, "mode")), preview.Revision); previews.Remove(preview.Token);
                }
                monitor.Note("info", "Excel/JSON参数或SPP包配置导入人：" + auth.User(session).DisplayName);
                Json(ctx.Response, new { ok = true, revision = monitor.DecoderRevision }, 200); return true;
            }
            if ((path == "/api/decoders/packet" || path == "/api/decoders/packet/delete") && method == "POST")
            {
                var body = Body(ctx.Request, 512000);
                lock (commandGate)
                {
                    var candidate = monitor.DecoderSnapshot(); string id;
                    if (path.EndsWith("/delete", StringComparison.Ordinal))
                    {
                        id = Text(body, "id"); if (candidate.Packets.RemoveAll(p => p.Id == id) == 0) throw new ArgumentException("SPP包不存在");
                    }
                    else
                    {
                        object value; if (!body.TryGetValue("packet", out value)) throw new ArgumentException("缺少SPP包定义");
                        PacketDefinition packet = JsonFile.Decode<PacketDefinition>(JsonFile.Encode(value));
                        if (packet == null) throw new ArgumentException("SPP包定义为空");
                        id = packet.Id; int index = candidate.Packets.FindIndex(p => p.Id == id);
                        if (index < 0) candidate.Packets.Add(packet); else candidate.Packets[index] = packet;
                    }
                    monitor.ApplyDecoder(candidate, Text(body, "revision"));
                    monitor.Note("info", "SPP包定义已更新：" + id + "；操作员：" + auth.User(session).DisplayName);
                }
                Json(ctx.Response, new { ok = true, revision = monitor.DecoderRevision }, 200); return true;
            }
            if (path == "/api/decoders/try" && method == "POST")
            {
                var body = Body(ctx.Request, 512000); DecoderConfiguration config = monitor.DecoderSnapshot(); object value;
                PacketDefinition definition;
                if (body.TryGetValue("packet", out value)) definition = JsonFile.Decode<PacketDefinition>(JsonFile.Encode(value));
                else definition = config.Packets.FirstOrDefault(p => p.Id == Text(body, "packetId"));
                if (definition == null) throw new ArgumentException("请选择SPP包定义");
                Json(ctx.Response, PacketCodec.TryDecode(config, definition, PacketCodec.Hex(Text(body, "hex"))), 200); return true;
            }
            Json(ctx.Response, new { error = "解码配置接口不存在" }, 404); return true;
        }
    }
}
