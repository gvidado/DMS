using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;

namespace TelemetryMonitor
{
    public sealed partial class Server
    {
        private static object ImportPreviewResponse(ImportPreview preview)
        {
            return new { token = preview.Token, revision = preview.Revision, editRevision = preview.EditRevision, configuration = preview.Configuration,
                sourceType = preview.SourceType, addition = preview.Addition, matching = preview.Matching, canReplace = preview.CanReplace,
                reviewErrors = preview.ReviewErrors, review = preview.Review == null ? null : preview.Review.Summary() };
        }
        private static void RebuildImportPreview(ImportPreview preview)
        {
            TelemetryCatalog incoming = preview.Review.RebuildCatalog();
            preview.CanReplace = false; preview.ReviewErrors.Clear();
            preview.Addition = new CatalogAddition(); preview.Matching = new CatalogMatchingReplacement();
            preview.AddConfiguration = null; preview.MatchingConfiguration = null;
            preview.Configuration = new DecoderConfiguration { Revision = preview.Revision, Catalog = incoming, Packets = preview.BaseConfiguration.Packets };
            if (preview.Review.ErrorCount > 0) preview.ReviewErrors.Add("还有" + preview.Review.ErrorCount + "项错误，请在参数预览中修改或舍弃对应参数，再重新检查");
            if (incoming.Parameters.Count == 0) preview.ReviewErrors.Add("至少需要保留一个参数，不能导入空参数表");
            if (preview.ReviewErrors.Count > 0)
            {
                preview.Addition.Errors.AddRange(preview.ReviewErrors); preview.Matching.Errors.AddRange(preview.ReviewErrors); return;
            }
            try { preview.Configuration = DecoderImport.Replace(preview.BaseConfiguration, incoming); preview.CanReplace = true; }
            catch (ArgumentException error) { preview.ReviewErrors.Add("替换配置检查失败：" + error.Message); }
            try { preview.AddConfiguration = DecoderImport.Add(preview.BaseConfiguration, incoming, out preview.Addition); }
            catch (ArgumentException error) { preview.AddConfiguration = null; preview.Addition.CanApply = false; preview.Addition.Errors.Add("添加配置检查失败：" + error.Message); }
            try { preview.MatchingConfiguration = DecoderImport.ReplaceMatching(preview.BaseConfiguration, incoming, out preview.Matching); }
            catch (ArgumentException error) { preview.MatchingConfiguration = null; preview.Matching.CanApply = false; preview.Matching.Errors.Add("同名替换检查失败：" + error.Message); }
        }
        private static object ImportRowDetail(TelemetryImportRow row)
        {
            return row == null ? null : new { key = row.Key, sheet = row.Sheet, row = row.Row, discarded = row.Discarded, values = row.Values, parameter = row.Parameter, issues = row.Issues };
        }
        private bool HandleTelemetryImportReview(HttpListenerContext ctx, Session session)
        {
            if (ctx.Request.Url.AbsolutePath != "/api/decoders/preview/parameter") return false;
            if (ctx.Request.HttpMethod == "GET")
            {
                lock (commandGate)
                {
                    ImportPreview preview = Preview(Query(ctx.Request, "token"), session);
                    if (preview.Review == null) throw new ArgumentException("网页修正适用于遥测Excel导入预览");
                    TelemetryImportRow row = preview.Review.Rows.FirstOrDefault(r => r.Key == Query(ctx.Request, "key"));
                    if (row == null) throw new ArgumentException("预览参数已不存在，请重新读取");
                    int index = preview.Review.Rows.IndexOf(row);
                    TelemetryImportRow previous = index > 0 && preview.Review.Rows[index - 1].SheetIndex == row.SheetIndex ? preview.Review.Rows[index - 1] : null;
                    TelemetryImportRow next = index + 1 < preview.Review.Rows.Count && preview.Review.Rows[index + 1].SheetIndex == row.SheetIndex ? preview.Review.Rows[index + 1] : null;
                    preview.Created = DateTime.UtcNow;
                    Json(ctx.Response, new { token = preview.Token, editRevision = preview.EditRevision, current = ImportRowDetail(row), previous = ImportRowDetail(previous), next = ImportRowDetail(next), suggestions = preview.Review.Suggestions(row, previous, next) }, 200);
                }
                return true;
            }
            if (ctx.Request.HttpMethod == "POST")
            {
                var body = Body(ctx.Request, 1024 * 1024); object value;
                var values = body.TryGetValue("values", out value) ? value as Dictionary<string, object> : null;
                if (values == null || values.Count != TelemetryImportReview.Fields.Length || TelemetryImportReview.Fields.Any(f => !values.ContainsKey(f) || !(values[f] is string))) throw new ArgumentException("请提交完整的参数编辑字段");
                if (values.Any(p => ((string)p.Value).Length > (p.Key == "物理意义" ? 100000 : 1000))) throw new ArgumentException("参数字段过长");
                if (!body.TryGetValue("discarded", out value) || !(value is bool)) throw new ArgumentException("请明确是否舍弃本参数");
                bool discarded = (bool)value;
                lock (commandGate)
                {
                    ImportPreview preview = Preview(Text(body, "token"), session);
                    if (preview.Review == null) throw new ArgumentException("网页修正适用于遥测Excel导入预览");
                    if (Text(body, "editRevision") != preview.EditRevision.ToString(System.Globalization.CultureInfo.InvariantCulture)) throw new InvalidOperationException("预览已被其他编辑更新，请关闭编辑框后重新打开");
                    TelemetryImportRow row = preview.Review.Rows.FirstOrDefault(r => r.Key == Text(body, "key"));
                    if (row == null) throw new ArgumentException("预览参数已不存在，请重新读取");
                    row.Values = values.ToDictionary(p => p.Key, p => (string)p.Value); row.Discarded = discarded;
                    RebuildImportPreview(preview); preview.EditRevision++; preview.Created = DateTime.UtcNow;
                    Json(ctx.Response, ImportPreviewResponse(preview), 200);
                }
                return true;
            }
            return false;
        }
    }
}
