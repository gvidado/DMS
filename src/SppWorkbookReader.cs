using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace TelemetryMonitor
{
    public sealed class SppWorkbookField
    {
        public int Row;
        public string Name;
        public string Field;
        public int Bits;
        public string Original;
        public int Value;
    }
    public sealed class SppWorkbookPreview
    {
        public string SourceFile;
        public string Sheet;
        public List<string> Sheets = new List<string>();
        public List<SppWorkbookField> Rows = new List<SppWorkbookField>();
        public Dictionary<string, int> Fields = new Dictionary<string, int>();
        public string IdentifierHex;
        public string IdentifierBits;
        public bool OuterSourceUnrestricted;
    }
    public sealed class SppWorkbookBatch
    {
        public string SourceFile;
        public List<SppWorkbookPreview> Packets = new List<SppWorkbookPreview>();
        public List<string> Errors = new List<string>();
    }
    public static partial class WorkbookReader
    {
        private static int SppValue(string text, string cell, int bits)
        {
            string value = (text ?? "").Trim();
            if (String.IsNullOrEmpty(value) || value.StartsWith("#EXCEL_ERROR:", StringComparison.Ordinal))
                throw new ArgumentException(cell + "：缺少有效值；公式单元格须先在Excel中计算并保存");
            // Binary APID takes precedence over the full 16-bit identifier also described in C5.
            MatchCollection binary = Regex.Matches(value, @"(?<![A-Za-z0-9])0[bB]\s*([01]+)(?![A-Za-z0-9])");
            MatchCollection hex = Regex.Matches(value, @"(?<![A-Za-z0-9])0[xX]\s*([0-9a-fA-F]+)(?![A-Za-z0-9])");
            try
            {
                // A full-width string containing only 0/1 (e.g. an 11-bit APID) is binary even without 0b.
                if (Regex.IsMatch(value, "^[01]{" + bits + "}$")) return Convert.ToInt32(value, 2);
                if (binary.Count > 0)
                {
                    if (binary.Count != 1) throw new ArgumentException(cell + "：存在多个二进制值，请仅保留该字段的值");
                    return checked((int)Convert.ToUInt32(binary[0].Groups[1].Value, 2));
                }
                if (hex.Count > 0)
                {
                    if (hex.Count != 1) throw new ArgumentException(cell + "：存在多个十六进制值，请仅保留该字段的值");
                    return checked((int)Convert.ToUInt32(hex[0].Groups[1].Value, 16));
                }
                // Prefer an explicitly annotated decimal over numbers in explanatory prose (e.g. length-1/length-7).
                MatchCollection annotated = Regex.Matches(value, @"(?<![\d.\-+])([0-9]+)\s*[（(]\s*十进制\s*[）)]");
                if (annotated.Count > 1) throw new ArgumentException(cell + "：存在多个十进制值，请仅保留该字段的值");
                Match number = annotated.Count == 1 ? annotated[0] : Regex.Match(value, @"^([0-9]+)(?=\s*$|\s*[，,；;（(。])");
                int result;
                if (number.Success && Int32.TryParse(number.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out result)) return result;
            }
            catch (OverflowException) { throw new ArgumentException(cell + "：数值过大"); }
            catch (FormatException) { throw new ArgumentException(cell + "：数值格式无效"); }
            throw new ArgumentException(cell + "：无法唯一识别字段值，请填写十进制整数、0b二进制或0x十六进制");
        }
        public static SppWorkbookPreview ReadSpp(byte[] bytes, string filename, string sheetName)
        {
            SppWorkbookBatch batch = ReadSppWorkbook(bytes, filename, sheetName, false);
            if (batch.Errors.Count > 0) throw new ArgumentException(batch.Errors[0]);
            return batch.Packets[0];
        }
        public static SppWorkbookBatch ReadAllSpp(byte[] bytes, string filename)
        { return ReadSppWorkbook(bytes, filename, null, true); }
        private static SppWorkbookBatch ReadSppWorkbook(byte[] bytes, string filename, string sheetName, bool all)
        {
            if (bytes == null || bytes.Length == 0 || bytes.Length > 8 * 1024 * 1024) throw new ArgumentException("SPP Excel不能为空且最大8MB");
            var batch = new SppWorkbookBatch { SourceFile = Path.GetFileName(filename) };
            using (var memory = new MemoryStream(bytes, false))
            using (var zip = new ZipArchive(memory, ZipArchiveMode.Read))
            {
                if (zip.Entries.Count > 3000 || zip.Entries.Sum(e => e.Length) > 64L * 1024 * 1024) throw new ArgumentException("Excel解压体积超出限制");
                var shared = new List<string>();
                if (zip.GetEntry("xl/sharedStrings.xml") != null)
                    foreach (XElement item in Xml(zip, "xl/sharedStrings.xml").Descendants(S + "si")) shared.Add(String.Concat(item.Descendants(S + "t").Select(t => t.Value)));
                XElement[] sheets = Xml(zip, "xl/workbook.xml").Descendants(S + "sheet").ToArray();
                if (sheets.Length == 0 || sheets.Length > 128) throw new ArgumentException("SPP Excel须包含1～128张工作表");
                XElement selected = String.IsNullOrEmpty(sheetName) ? sheets[0] : sheets.FirstOrDefault(s => (string)s.Attribute("name") == sheetName);
                if (selected == null) throw new ArgumentException("找不到SPP工作表：" + sheetName);
                XDocument relations = Xml(zip, "xl/_rels/workbook.xml.rels");
                foreach (XElement sheet in all ? sheets : new[] { selected })
                {
                    try { batch.Packets.Add(ReadSppSheet(zip, relations, shared, sheets, sheet, batch.SourceFile)); }
                    catch (ArgumentException ex) { batch.Errors.Add(ex.Message); }
                    catch (System.Xml.XmlException) { batch.Errors.Add((string)sheet.Attribute("name") + "：工作表XML损坏"); }
                }
            }
            return batch;
        }
        private static SppWorkbookPreview ReadSppSheet(ZipArchive zip, XDocument relations, List<string> shared, XElement[] sheets, XElement selected, string filename)
        {
                var result = new SppWorkbookPreview { SourceFile = filename };
                result.Sheets.AddRange(sheets.Select(s => (string)s.Attribute("name")));
                result.Sheet = (string)selected.Attribute("name");
                string rid = (string)selected.Attribute(R + "id");
                XElement relation = relations.Root.Elements().FirstOrDefault(e => (string)e.Attribute("Id") == rid);
                if (relation == null || (string)relation.Attribute("TargetMode") == "External") throw new ArgumentException("SPP工作表关联无效");
                var uri = new Uri(new Uri("http://xlsx.local/xl/workbook.xml"), (string)relation.Attribute("Target"));
                if (uri.Host != "xlsx.local" || !uri.AbsolutePath.StartsWith("/xl/", StringComparison.Ordinal)) throw new ArgumentException("SPP工作表路径无效");
                var cells = new Dictionary<string, string>();
                foreach (XElement c in Xml(zip, uri.AbsolutePath.TrimStart('/')).Descendants(S + "c"))
                {
                    string address = (string)c.Attribute("r") ?? "";
                    if (Regex.IsMatch(address, @"^[ABC](?:[1-9]|[1-9][0-9])$")) cells[address] = CellValue(c, shared).Trim();
                }
                string sourceLabel;bool hasSource=cells.TryGetValue("A2",out sourceLabel) && new[]{"包来源","数据源","外层数据源","数据源标识","外层数据源标识"}.Contains(Regex.Replace(sourceLabel,@"\s|[:：]",""));
                result.Fields["OuterSourceId"]=-1;
                if(hasSource)
                {
                    string width,value;cells.TryGetValue("B2",out width);cells.TryGetValue("C2",out value);
                    if(!Regex.IsMatch(width??"",@"^\s*(?:2\s*(?:字节|bytes?)|16\s*(?:bits?|位))\s*$",RegexOptions.IgnoreCase))throw new ArgumentException(result.Sheet+"!B2：数据源长度应为2字节或16 bit");
                    string hex=Regex.Replace(value??"",@"\s","");if(hex.StartsWith("0x",StringComparison.OrdinalIgnoreCase))hex=hex.Substring(2);
                    result.OuterSourceUnrestricted=hex=="/";
                    if(!result.OuterSourceUnrestricted&&!Regex.IsMatch(hex,@"^[0-9A-Fa-f]{1,4}$"))throw new ArgumentException(result.Sheet+"!C2：数据源应为十六进制标识，例如0202、0422或0x0802；仅监测原包可填/");
                    int source=result.OuterSourceUnrestricted?-1:Convert.ToInt32(hex,16);result.Fields["OuterSourceId"]=source;
                    result.Rows.Add(new SppWorkbookField{Row=2,Name="外层数据源",Field="OuterSourceId",Bits=16,Original=value,Value=source});
                }
                int[] rows = { 2, 3, 4, 5, 8, 11, 12, 16, 17 };
                if(hasSource)rows=rows.Select(row=>row+1).ToArray();
                int[] widths = { 3, 1, 1, 11, 16, 8, 8, 16, 16 };
                string[] keys = { "Version", "PacketType", "SecondaryHeader", "Apid", "ExpectedLengthField", "ServiceType", "ServiceSubtype", "SourceAddress", "DestinationAddress" };
                string[] names = { "包版本号", "包类型", "副导头标志", "APID", "包数据长度", "业务类型", "业务子类型", "源地址", "目的地址" };
                for (int i = 0; i < rows.Length; i++)
                {
                    string lengthText, original, b = "B" + rows[i], c = "C" + rows[i];
                    cells.TryGetValue(b, out lengthText); cells.TryGetValue(c, out original);
                    Match length = Regex.Match(lengthText ?? "", @"^\s*([0-9]+)\s*(?:bits?|位)\s*$", RegexOptions.IgnoreCase);
                    int bits;
                    if (!length.Success || !Int32.TryParse(length.Groups[1].Value, out bits) || bits != widths[i])
                        throw new ArgumentException(result.Sheet + "!" + b + "：" + names[i] + "的长度应为" + widths[i] + " bit，实际为“" + (lengthText ?? "空白") + "”");
                    int value = SppValue(original, result.Sheet + "!" + c, bits);
                    if (value < 0 || value >= (1 << bits)) throw new ArgumentException(result.Sheet + "!" + c + "：" + names[i] + "超出" + bits + " bit范围");
                    result.Fields[keys[i]] = value;
                    result.Rows.Add(new SppWorkbookField { Row = rows[i], Name = names[i], Field = keys[i], Bits = bits, Original = original, Value = value });
                }
            int identifier = (result.Fields["Version"] << 13) | (result.Fields["PacketType"] << 12) | (result.Fields["SecondaryHeader"] << 11) | result.Fields["Apid"];
            result.IdentifierHex = "0x" + identifier.ToString("X4");
            result.IdentifierBits = Convert.ToString(identifier, 2).PadLeft(16, '0');
            return result;
        }
    }
}
