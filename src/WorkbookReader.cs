using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace TelemetryMonitor
{
    // Read-only OOXML importer: no Excel installation, Python runtime or macro execution.
    public static partial class WorkbookReader
    {
        private static readonly XNamespace S = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        private static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        private static XDocument Xml(ZipArchive zip, string name)
        {
            ZipArchiveEntry entry = zip.GetEntry(name);
            if (entry == null || entry.Length > 24 * 1024 * 1024) throw new ArgumentException("Excel缺少必要内容或内容过大：" + name);
            using (Stream stream = entry.Open())
            using (XmlReader reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 24 * 1024 * 1024 })) return XDocument.Load(reader);
        }
        private static int Column(string cell)
        {
            int col = 0;
            foreach (char c in cell) { if (c < 'A' || c > 'Z') break; col = checked(col * 26 + c - 'A' + 1); }
            return col;
        }
        private static string CellValue(XElement cell, List<string> shared)
        {
            string type = (string)cell.Attribute("t") ?? "";
            if (type == "inlineStr") return String.Concat(cell.Descendants(S + "t").Select(t => t.Value));
            string value = (string)cell.Element(S + "v") ?? "";
            if (type == "e") return "#EXCEL_ERROR:" + value;
            if (type == "s")
            {
                int index; if (!Int32.TryParse(value, out index) || index < 0 || index >= shared.Count) throw new ArgumentException("Excel共享字符串索引无效");
                return shared[index];
            }
            return value;
        }
        private static string Key(string text) { return Regex.Replace(text ?? "", "\\s+", "").Trim(); }
        private static bool Numeric(string s, out double n) { return Double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out n) && !Double.IsInfinity(n) && !Double.IsNaN(n); }
        private static double Number(string s) { double n; if (!Numeric(s, out n)) throw new ArgumentException("非有效数字：" + s); return n; }
        private static double EnumNumber(string s)
        {
            bool negative = s.StartsWith("-"); string v = negative ? s.Substring(1) : s;
            if (v.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) return (negative ? -1d : 1d) * Convert.ToUInt64(v.Substring(2), 16);
            return Number(s);
        }
        private static void Issue(TelemetryCatalog catalog, string level, string sheet, int row, string code, string message)
        { catalog.Issues.Add(new ImportIssue { Level = level, Sheet = sheet, Row = row, Code = code, Message = message }); }
        private static void Disable(TelemetryCatalog c, Parameter p, string reason)
        {
            p.Supported = false; p.Diagnostic = String.IsNullOrEmpty(p.Diagnostic) ? reason : p.Diagnostic + "；" + reason;
            Issue(c, "error", p.Group, p.SourceRow, p.Code, reason);
        }
        private static void ReadEnums(TelemetryCatalog catalog, Parameter parameter)
        {
            const string num = "-?(?:0[xX][0-9a-fA-F]+|[0-9]+(?:\\.[0-9]+)?)";
            string token = "(?:^|[;；\\r\\n]|(?=\\[))\\s*(?:\\[\\s*(?<lo>" + num + ")\\s*,\\s*(?<hi>" + num + ")\\s*\\]|(?<one>" + num + "))\\s*[:：]";
            MatchCollection matches = Regex.Matches(parameter.Note ?? "", token);
            for (int i = 0; i < matches.Count; i++)
            {
                Match match = matches[i];
                int begin = match.Index + match.Length, end = i + 1 < matches.Count ? matches[i + 1].Index : parameter.Note.Length;
                string text = parameter.Note.Substring(begin, end - begin).Trim(' ', '\r', '\n', ';', '；');
                if (text.Length == 0) continue;
                double lo = EnumNumber(match.Groups["lo"].Success ? match.Groups["lo"].Value : match.Groups["one"].Value);
                double hi = match.Groups["hi"].Success ? EnumNumber(match.Groups["hi"].Value) : lo;
                if (lo > hi) { Issue(catalog, "warning", parameter.Group, parameter.SourceRow, parameter.Code, "枚举范围上下限颠倒，未启用该含义"); continue; }
                if (lo != hi) { parameter.EnumRanges.Add(new EnumRange { Min = lo, Max = hi, Text = text }); continue; }
                string key = lo.ToString("0.################", CultureInfo.InvariantCulture), previous;
                if (parameter.Enums.TryGetValue(key, out previous) && previous != text)
                {
                    parameter.Enums[key] = previous + " / " + text;
                    Issue(catalog, "warning", parameter.Group, parameter.SourceRow, parameter.Code, "枚举值" + key + "有多个含义，保留并列含义");
                }
                else parameter.Enums[key] = text;
            }
        }
        private static Parameter ParameterFromRow(TelemetryCatalog catalog, SheetSummary summary, int row, Dictionary<string, string> values)
        {
            Func<string, string> value = key => values.ContainsKey(key) ? values[key].Trim() : "";
            string code = value("参数代号"), name = value("参数名称");
            if (name == "预留") { summary.Skipped++; summary.ReservedSkipped++; return null; }
            if (String.IsNullOrEmpty(code) && String.IsNullOrEmpty(name)) { summary.Skipped++; return null; }
            double width, start;
            if (Numeric(value("字节个数"), out width) && width == 0) { summary.Skipped++; return null; }
            if (String.IsNullOrEmpty(code) || String.IsNullOrEmpty(name)) throw new ArgumentException(summary.Name + "!" + row + "：参数代号和名称必须完整，不能生成可靠ID");
            var p = new Parameter { Id = summary.Name + "/" + code, SourceId = summary.Name + "/" + code, Code = code, Name = name, Group = summary.Name,
                SourceRow = row, SourceSheetIndex = summary.Index, Position = value("位置"), Note = value("物理意义"),
                Unit = value("单位"), Format = value("缺省显示类型"), Diagnostic = "" };
            if (p.Unit == "无" || p.Unit == "/") p.Unit = "";
            if (String.IsNullOrEmpty(p.Format)) p.Format = "十进制";
            if (!Numeric(value("起始字节"), out start) || !Numeric(value("字节个数"), out width) || start < 0 || start > 65507 || width <= 0 || width > 8)
            { p.Length = 1; Disable(catalog, p, "起始字节/字节个数缺失、无缓存值或越界"); return p; }
            double rawBits = width * 8, rawStart = (start - Math.Floor(start)) * 8;
            p.Offset = (int)Math.Floor(start);
            if (Math.Abs(rawBits - Math.Round(rawBits)) > 0.00001 || Math.Abs(rawStart - Math.Round(rawStart)) > 0.00001)
            { p.Length = 1; Disable(catalog, p, "字节位置或宽度不是1/8字节的整数倍"); return p; }
            bool bits = width != Math.Floor(width) || start != Math.Floor(start) || Regex.IsMatch(value("起始位"), "^B[0-7]$");
            if (bits)
            {
                p.BitStart = (int)Math.Round(rawStart); p.BitCount = (int)Math.Round(rawBits); p.Length = (p.BitStart + p.BitCount + 7) / 8;
                Match explicitBit = Regex.Match(value("起始位"), "^B([0-7])$");
                if (explicitBit.Success && Int32.Parse(explicitBit.Groups[1].Value) != p.BitStart) Disable(catalog, p, "小数字节位置与起始位不一致");
                p.CrossByte = p.Length > 1;
            }
            else p.Length = (int)width;
            Match location = Regex.Match(p.Position, "^Byte(?<a>[0-9]+)(?:_B(?<ab>[0-7]))?\\s*[~～]\\s*Byte(?<b>[0-9]+)(?:_B(?<bb>[0-7]))?$");
            if (location.Success)
            {
                int a = Int32.Parse(location.Groups["a"].Value), b = Int32.Parse(location.Groups["b"].Value);
                int ab = location.Groups["ab"].Success ? Int32.Parse(location.Groups["ab"].Value) : 0;
                int bb = location.Groups["bb"].Success ? Int32.Parse(location.Groups["bb"].Value) : 7;
                if (a != p.Offset || (p.BitStart >= 0 && ab != p.BitStart) || ((b - a) * 8 + bb - ab + 1) != (int)Math.Round(rawBits)) Disable(catalog, p, "位置列与起始字节/宽度不一致");
            }
            else if (!String.IsNullOrEmpty(p.Position)) Issue(catalog, "warning", p.Group, row, code, "位置列无法识别，使用起始字节、字节个数、起始位");
            string type = value("数据类型");
            p.Type = type.Contains("无符号") ? "uint" : type.Contains("补码") || type.Contains("有符号") ? "int" : type.Contains("单精度") ? "float32" : type.Contains("双精度") ? "float64" : type.IndexOf("ASCII", StringComparison.OrdinalIgnoreCase) >= 0 ? "ascii" : type.IndexOf("BCD", StringComparison.OrdinalIgnoreCase) >= 0 ? "bcd" : "unknown";
            if (p.Type == "unknown") Disable(catalog, p, "数据类型缺失或不支持：" + type);
            if ((p.Type == "float32" && p.Length != 4) || (p.Type == "float64" && p.Length != 8)) Disable(catalog, p, "浮点类型与字节宽度不符");
            if (p.Length > 8) Disable(catalog, p, "跨字节字段占用超过8字节");
            // Column M is deliberately ignored. The type defines decoding and column N defines scaling.
            string coefficientText = value("系数");
            if (String.IsNullOrEmpty(coefficientText)) coefficientText = value("公式系数");
            if (!String.IsNullOrEmpty(coefficientText) && coefficientText != "/" && coefficientText != "／")
            {
                double coefficient;
                if (Numeric(coefficientText, out coefficient)) p.Scale = coefficient;
                else Disable(catalog, p, "N列系数不是有效数值：" + coefficientText);
            }
            double precision;
            if (Numeric(value("精度"), out precision)) p.Precision = Math.Max(0, Math.Min(12, (int)precision));
            ReadEnums(catalog, p); return p;
        }
        public static TelemetryCatalog Read(byte[] bytes, string filename)
        { return ReadCore(bytes, filename, null); }
        public static TelemetryImportReview ReadEditable(byte[] bytes, string filename)
        {
            var review = new TelemetryImportReview();
            review.Template = ReadCore(bytes, filename, review);
            review.RebuildCatalog(); return review;
        }
        private static TelemetryCatalog ReadCore(byte[] bytes, string filename, TelemetryImportReview review)
        {
            if (bytes == null || bytes.Length > 8 * 1024 * 1024) throw new ArgumentException("Excel文件最大8MB");
            var catalog = new TelemetryCatalog { SourceFile = Path.GetFileName(filename), GeneratedAt = BeijingTime.Now.ToString("yyyy-MM-dd HH:mm:ss") };
            catalog.Assumptions.Add("按工作簿顺序跳过第1张表，从第2张表开始，不依赖工作表名称");
            catalog.Assumptions.Add("地址按起始字节与位置列核对；小数1/8字节表示一个位；结束字节列不用于计算位字段");
            catalog.Assumptions.Add("完全忽略M列；按数据类型解码后乘以N列系数；系数空白或/时按1处理，不生成公式标识");
            catalog.Assumptions.Add("D列参数名称去除首尾空白后等于预留时跳过，不重排其他参数的原始字节地址");
            catalog.Assumptions.Add("跨字节位字段首位对应结果最高位，按位置列顺序由高到低拼接（firstMsb）");
            catalog.Assumptions.Add("Byte0基准、APID归属由SPP包定义指定，不根据表名猜测；B0的物理位编号与结果位的排列分别配置");
            using (var memory = new MemoryStream(bytes, false))
            using (var zip = new ZipArchive(memory, ZipArchiveMode.Read))
            {
                if (zip.Entries.Count > 3000 || zip.Entries.Sum(e => e.Length) > 64L * 1024 * 1024) throw new ArgumentException("Excel解压体积超出限制");
                var shared = new List<string>();
                if (zip.GetEntry("xl/sharedStrings.xml") != null)
                    foreach (XElement item in Xml(zip, "xl/sharedStrings.xml").Descendants(S + "si")) shared.Add(String.Concat(item.Descendants(S + "t").Select(t => t.Value)));
                var rel = Xml(zip, "xl/_rels/workbook.xml.rels").Root.Elements().ToDictionary(e => (string)e.Attribute("Id"), e => e);
                XElement[] sheets = Xml(zip, "xl/workbook.xml").Descendants(S + "sheet").ToArray();
                if (sheets.Length < 2 || sheets.Length > 128) throw new ArgumentException("Excel至少包含两张工作表，且不超过128张");
                for (int index = 1; index < sheets.Length; index++)
                {
                    string sheetName = (string)sheets[index].Attribute("name");
                    var summary = new SheetSummary { Index = index + 1, Name = sheetName }; catalog.Sheets.Add(summary);
                    string rid = (string)sheets[index].Attribute(R + "id"); XElement relation;
                    if (rid == null || !rel.TryGetValue(rid, out relation) || (string)relation.Attribute("TargetMode") == "External") throw new ArgumentException("工作表关联无效：" + sheetName);
                    var uri = new Uri(new Uri("http://xlsx.local/xl/workbook.xml"), (string)relation.Attribute("Target"));
                    if (uri.Host != "xlsx.local" || !uri.AbsolutePath.StartsWith("/xl/", StringComparison.Ordinal)) throw new ArgumentException("Excel工作表路径无效");
                    XDocument sheet = Xml(zip, uri.AbsolutePath.TrimStart('/'));
                    var headers = new Dictionary<int, string>(); int rowCount = 0;
                    foreach (XElement row in sheet.Descendants(S + "row"))
                    {
                        if (++rowCount > 25000) throw new ArgumentException("单表超过25000行");
                        int rowNumber; if (!Int32.TryParse((string)row.Attribute("r"), out rowNumber)) rowNumber = rowCount;
                        var cells = new Dictionary<int, string>();
                        foreach (XElement cell in row.Elements(S + "c"))
                        { int column = Column((string)cell.Attribute("r") ?? ""); if (column > 0 && column <= 256) cells[column] = CellValue(cell, shared); }
                        if (headers.Count == 0)
                        {
                            if (rowCount > 50) break;
                            var candidate = cells.ToDictionary(p => p.Key, p => Key(p.Value) == "占用参数代号" ? "参数代号" : Key(p.Value));
                            if (new[] { "参数代号", "参数名称", "起始字节", "字节个数", "数据类型" }.All(h => candidate.Values.Contains(h)))
                            { headers = candidate; summary.HeaderRow = rowNumber; }
                            continue;
                        }
                        var values = new Dictionary<string, string>();
                        foreach (var header in headers) { string v; if (!String.IsNullOrEmpty(header.Value)) values[header.Value] = cells.TryGetValue(header.Key, out v) ? v : ""; }
                        if (review != null)
                        {
                            string name, code; values.TryGetValue("参数名称", out name); values.TryGetValue("参数代号", out code);
                            if ((name ?? "").Trim() == "预留") { summary.Skipped++; summary.ReservedSkipped++; continue; }
                            if (String.IsNullOrWhiteSpace(name) && String.IsNullOrWhiteSpace(code) && !new[] { "起始字节", "字节个数", "位置", "数据类型" }.Any(field => values.ContainsKey(field) && !String.IsNullOrWhiteSpace(values[field]))) { summary.Skipped++; continue; }
                            review.Rows.Add(new TelemetryImportRow { Key = "row-" + review.Rows.Count, Sheet = sheetName, SheetIndex = index + 1, Row = rowNumber,
                                Values = TelemetryImportReview.Fields.ToDictionary(field => field, field => values.ContainsKey(field) ? values[field] : (field == "系数" && values.ContainsKey("公式系数") ? values["公式系数"] : "")) });
                            if (review.Rows.Count > 20000) throw new ArgumentException("遥测参数超过20000项");
                            continue;
                        }
                        Parameter parameter = ParameterFromRow(catalog, summary, rowNumber, values);
                        if (parameter != null)
                        {
                            catalog.Parameters.Add(parameter); summary.Parameters++; if (parameter.Supported) summary.Supported++;
                            if (catalog.Parameters.Count > 20000) throw new ArgumentException("遥测参数超过20000项");
                        }
                    }
                    if (summary.HeaderRow == 0) Issue(catalog, "warning", sheetName, 0, "", "未找到参数表头，未读取此表");
                }
            }
            if (review != null)
            {
                if (review.Rows.Count == 0) throw new ArgumentException("第2张及后续工作表中未找到可读取的参数行，请检查表头和参数名称/代号");
                return catalog;
            }
            if (catalog.Parameters.Count == 0) throw new ArgumentException("第2张及后续工作表中未找到有效参数定义");
            var duplicate = catalog.Parameters.GroupBy(p => p.Id).FirstOrDefault(g => g.Count() > 1);
            if (duplicate != null) throw new ArgumentException("同一表内参数代号重复：" + duplicate.Key);
            return catalog;
        }
    }
}
