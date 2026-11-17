using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace TelemetryMonitor
{
    // Draft rows remain separate from saved decoding definitions, including discarded rows.
    public sealed class TelemetryImportRow
    {
        public string Key, Sheet;
        public int SheetIndex, Row;
        public Dictionary<string, string> Values;
        public bool Discarded;
        public Parameter Parameter;
        public List<ImportIssue> Issues = new List<ImportIssue>();
        public string Value(string name) { string result; return Values.TryGetValue(name, out result) ? (result ?? "").Trim() : ""; }
        public void Error(string message) { Issues.Add(new ImportIssue { Level = "error", Sheet = Sheet, Row = Row, Code = Value("参数代号"), Message = message }); }
    }
    public sealed class TelemetryImportSuggestion
    {
        public string Title, Reason;
        public Dictionary<string, string> Values = new Dictionary<string, string>();
    }
    public sealed class TelemetryImportReview
    {
        public static readonly string[] Fields = { "参数代号", "参数名称", "起始字节", "字节个数", "起始位", "位置", "数据类型", "系数", "单位", "缺省显示类型", "精度", "物理意义" };
        public TelemetryCatalog Template;
        public List<TelemetryImportRow> Rows = new List<TelemetryImportRow>();
        public int ErrorCount { get { return Rows.Where(r => !r.Discarded).Sum(r => r.Issues.Count(i => i.Level == "error")); } }
        public TelemetryCatalog RebuildCatalog()
        {
            var catalog = JsonFile.Decode<TelemetryCatalog>(JsonFile.Encode(Template));
            catalog.Parameters.Clear();
            foreach (SheetSummary sheet in catalog.Sheets) { sheet.Parameters = 0; sheet.Supported = 0; }
            foreach (TelemetryImportRow row in Rows) WorkbookReader.ParseImportRow(row);
            foreach (var duplicate in Rows.Where(r => !r.Discarded && r.Value("参数代号").Length > 0).GroupBy(r => r.Sheet + "\0" + r.Value("参数代号")).Where(g => g.Count() > 1))
                foreach (TelemetryImportRow row in duplicate) row.Error("同一表内参数代号重复，涉及Excel第" + String.Join("、", duplicate.Select(r => r.Row)) + "行；请修改代号或舍弃重复项");
            foreach (TelemetryImportRow row in Rows.Where(r => !r.Discarded))
            {
                // Review IDs cannot collide, even while the original code is missing or duplicated.
                if (row.Issues.Any(i => i.Level == "error"))
                {
                    row.Parameter.Id = "@review/" + row.Key; row.Parameter.Supported = false;
                    row.Parameter.Diagnostic = String.Join("；", row.Issues.Where(i => i.Level == "error").Select(i => i.Message));
                }
                catalog.Parameters.Add(row.Parameter); catalog.Issues.AddRange(row.Issues);
                SheetSummary sheet = catalog.Sheets.First(s => s.Index == row.SheetIndex);
                sheet.Parameters++; if (!row.Issues.Any(i => i.Level == "error")) sheet.Supported++;
            }
            return catalog;
        }
        public object Summary()
        {
            return new { total = Rows.Count, discarded = Rows.Count(r => r.Discarded), errors = ErrorCount,
                rows = Rows.Select(r => new { key = r.Key, sheet = r.Sheet, row = r.Row, code = r.Value("参数代号"), name = r.Value("参数名称"), discarded = r.Discarded,
                    position = r.Value("位置"), start = r.Value("起始字节"), width = r.Value("字节个数"), type = r.Value("数据类型"), coefficient = r.Value("系数"),
                    parameterId = r.Parameter.Id, errors = r.Issues.Count(i => i.Level == "error"), issues = r.Issues }).ToArray() };
        }
        private static bool Interval(TelemetryImportRow row, out double start, out double end)
        {
            start = end = 0; double width;
            if (row == null || !Double.TryParse(row.Value("起始字节"), NumberStyles.Float, CultureInfo.InvariantCulture, out start) || !Double.TryParse(row.Value("字节个数"), NumberStyles.Float, CultureInfo.InvariantCulture, out width)) return false;
            start *= 8; end = start + width * 8;
            return start >= 0 && end > start && end <= 65507 * 8 && Math.Abs(start - Math.Round(start)) < .00001 && Math.Abs(end - Math.Round(end)) < .00001;
        }
        private static string N(double value) { return value.ToString("0.########", CultureInfo.InvariantCulture); }
        private static Dictionary<string, string> Address(double start, double end)
        {
            int a = (int)start, b = (int)end - 1;
            return new Dictionary<string, string> { { "起始字节", N(start / 8) }, { "字节个数", N((end - start) / 8) },
                { "起始位", a % 8 == 0 && (b + 1) % 8 == 0 ? "" : "B" + a % 8 },
                { "位置", "Byte" + a / 8 + (a % 8 == 0 && (b + 1) % 8 == 0 ? "" : "_B" + a % 8) + "~Byte" + b / 8 + (a % 8 == 0 && (b + 1) % 8 == 0 ? "" : "_B" + b % 8) } };
        }
        public List<TelemetryImportSuggestion> Suggestions(TelemetryImportRow row, TelemetryImportRow previous, TelemetryImportRow next)
        {
            var result = new List<TelemetryImportSuggestion>();
            Match location = Regex.Match(row.Value("位置"), "^Byte(?<a>[0-9]{1,5})(?:_B(?<ab>[0-7]))?\\s*[~～]\\s*Byte(?<b>[0-9]{1,5})(?:_B(?<bb>[0-7]))?$");
            if (location.Success)
            {
                int a = Int32.Parse(location.Groups["a"].Value), b = Int32.Parse(location.Groups["b"].Value);
                double start = a * 8 + (location.Groups["ab"].Success ? Int32.Parse(location.Groups["ab"].Value) : 0), end = b * 8 + (location.Groups["bb"].Success ? Int32.Parse(location.Groups["bb"].Value) : 7) + 1;
                double originalStart, originalEnd;
                if (end > start && end - start <= 64 && end <= 65507 * 8 && (!Interval(row, out originalStart, out originalEnd) || originalStart != start || originalEnd != end || (Regex.IsMatch(row.Value("起始位"), "^B[0-7]$") && row.Value("起始位") != "B" + (int)start % 8)))
                    result.Add(new TelemetryImportSuggestion { Title = "按当前位置列核对地址", Reason = "位置列为" + row.Value("位置") + "，可换算为以下字节及位定义；请确认位置列本身正确。", Values = Address(start, end) });
            }
            double ps, pe, ns, ne, rs, re;
            if (previous != null && next != null && !previous.Discarded && !next.Discarded && Interval(previous, out ps, out pe) && Interval(next, out ns, out ne) && ns > pe && ns - pe <= 64 && (!Interval(row, out rs, out re) || rs != pe || re != ns))
                result.Add(new TelemetryImportSuggestion { Title = "参考上下参数之间的地址空隙", Reason = "上一参数结束于第" + N(pe / 8) + "字节之前，下一参数从第" + N(ns / 8) + "字节开始。仅在三项连续、没有预留区或地址复用时采纳。", Values = Address(pe, ns) });
            if (previous != null && next != null && !previous.Discarded && !next.Discarded)
            {
                if (row.Parameter.Type == "unknown" && previous.Value("数据类型") == next.Value("数据类型") && previous.Value("数据类型").Length > 0)
                    result.Add(new TelemetryImportSuggestion { Title = "参考上下参数的数据类型", Reason = "相邻两项均为“" + previous.Value("数据类型") + "”。这只是上下文参考，请核实本参数定义。", Values = new Dictionary<string, string> { { "数据类型", previous.Value("数据类型") } } });
                double coefficient, pCoefficient, nCoefficient;
                if (row.Value("系数").Length > 0 && row.Value("系数") != "/" && row.Value("系数") != "／" && (!Double.TryParse(row.Value("系数"), NumberStyles.Float, CultureInfo.InvariantCulture, out coefficient) || Double.IsNaN(coefficient) || Double.IsInfinity(coefficient)) && Double.TryParse(previous.Value("系数"), NumberStyles.Float, CultureInfo.InvariantCulture, out pCoefficient) && Double.TryParse(next.Value("系数"), NumberStyles.Float, CultureInfo.InvariantCulture, out nCoefficient) && pCoefficient == nCoefficient && !Double.IsNaN(pCoefficient) && !Double.IsInfinity(pCoefficient))
                    result.Add(new TelemetryImportSuggestion { Title = "参考上下参数的相同系数", Reason = "相邻两项系数均为" + N(pCoefficient) + "，不能据此证明本项系数相同，请对照遥测定义确认。", Values = new Dictionary<string, string> { { "系数", N(pCoefficient) } } });
                Match pcode = Regex.Match(previous.Value("参数代号"), "^(?<prefix>.*?)(?<number>[0-9]{1,8})$"), ncode = Regex.Match(next.Value("参数代号"), "^(?<prefix>.*?)(?<number>[0-9]{1,8})$");
                if (row.Value("参数代号").Length == 0 && pcode.Success && ncode.Success && pcode.Groups["prefix"].Value == ncode.Groups["prefix"].Value && Int32.Parse(ncode.Groups["number"].Value) == Int32.Parse(pcode.Groups["number"].Value) + 2)
                {
                    string code = pcode.Groups["prefix"].Value + (Int32.Parse(pcode.Groups["number"].Value) + 1).ToString(new String('0', pcode.Groups["number"].Length));
                    if (!Rows.Any(r => !r.Discarded && r.Sheet == row.Sheet && r.Value("参数代号") == code)) result.Add(new TelemetryImportSuggestion { Title = "参考相邻代号补齐编号", Reason = "上一项为" + previous.Value("参数代号") + "，下一项为" + next.Value("参数代号") + "；请确认本表代号按此连续编号。", Values = new Dictionary<string, string> { { "参数代号", code } } });
                }
            }
            int width = row.Value("数据类型").Contains("单精度") ? 4 : row.Value("数据类型").Contains("双精度") ? 8 : 0;
            if (width > 0 && Interval(row, out rs, out re) && re - rs != width * 8)
                result.Add(new TelemetryImportSuggestion { Title = "按浮点类型核对宽度", Reason = row.Value("数据类型") + "应占" + width + "字节；请同时检查是否与相邻参数重叠。", Values = Address(rs, rs + width * 8) });
            return result;
        }
    }
    public static partial class WorkbookReader
    {
        public static void ParseImportRow(TelemetryImportRow row)
        {
            row.Issues.Clear();
            var values = new Dictionary<string, string>(row.Values);
            if (row.Value("参数代号").Length == 0) { row.Error("参数代号缺失"); values["参数代号"] = "@missing-" + row.Key; }
            if (row.Value("参数名称").Length == 0) { row.Error("参数名称缺失"); values["参数名称"] = "（名称待填写）"; }
            if (row.Value("参数名称") == "预留") { row.Error("名称为预留；如无需导入，请勾选舍弃本参数"); values["参数名称"] = "（预留待舍弃）"; }
            if (row.Value("参数代号").Length + row.Sheet.Length + 1 > 240) row.Error("工作表名与参数代号合计超过240个字符");
            if (row.Value("参数名称").Length > 240 || row.Value("单位").Length > 100 || row.Value("物理意义").Length > 100000) row.Error("名称、单位或物理意义过长");
            double precision;
            string precisionText = row.Value("精度");
            if (precisionText.Length > 0 && precisionText != "/" && precisionText != "／" && !Numeric(precisionText, out precision)) row.Error("精度应为空白、/或有效数值，建议使用0～12的整数");
            string bit = row.Value("起始位"); double numericBit;
            if (bit.Length > 0 && !Regex.IsMatch(bit, "^B[0-7]$") && bit != "/" && bit != "／" && !(Numeric(bit, out numericBit) && numericBit == 0)) row.Error("起始位应为空白、0（整字节）或B0～B7");
            var temporary = new TelemetryCatalog();
            try
            {
                // A named zero-width parameter needs correction rather than disappearing silently.
                if (Numeric(row.Value("字节个数"), out precision) && precision == 0) { row.Error("字节个数为0，请修正或舍弃本参数"); values["字节个数"] = "无效"; }
                row.Parameter = ParameterFromRow(temporary, new SheetSummary { Name = row.Sheet, Index = row.SheetIndex }, row.Row, values);
                if (row.Parameter == null) throw new ArgumentException("参数定义无法生成，请补齐字段或舍弃");
                row.Issues.AddRange(temporary.Issues);
                if (Numeric(precisionText, out precision) && (precision < 0 || precision > 12 || precision != Math.Floor(precision)))
                    row.Issues.Add(new ImportIssue { Level = "warning", Sheet = row.Sheet, Row = row.Row, Code = row.Value("参数代号"), Message = "精度" + precisionText + "沿用原有显示规则转换为" + row.Parameter.Precision + "，可在预览中改为0～12的整数" });
                if (row.Parameter.Offset + row.Parameter.Length > 65507) row.Error("参数地址与宽度超出UDP载荷上限");
                if (row.Parameter.BitStart >= 0 && new[] { "float32", "float64", "ascii", "bcd" }.Contains(row.Parameter.Type)) row.Error("浮点、ASCII和BCD不支持位字段，请核对起始位及宽度");
                if (row.Parameter.Enums.Count > 4096 || row.Parameter.EnumRanges.Count > 4096) row.Error("枚举含义超过4096项");
            }
            catch (Exception error)
            {
                if (!(error is ArgumentException) && !(error is OverflowException) && !(error is FormatException)) throw;
                row.Error("参数解析失败：" + error.Message);
                row.Parameter = new Parameter { Id = "@review/" + row.Key, SourceId = row.Sheet + "/" + row.Value("参数代号"), Code = row.Value("参数代号"), Name = row.Value("参数名称"), Group = row.Sheet, SourceRow = row.Row, SourceSheetIndex = row.SheetIndex, Length = 1, Type = "unknown", Supported = false };
            }
            if (row.Issues.Any(i => i.Level == "error")) { row.Parameter.Supported = false; row.Parameter.Diagnostic = String.Join("；", row.Issues.Where(i => i.Level == "error").Select(i => i.Message)); }
        }
    }
}
