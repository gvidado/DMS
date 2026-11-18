using System;
using System.Collections.Generic;
using System.Linq;

namespace TelemetryMonitor
{
    public sealed class CatalogAddition
    {
        public bool CanApply;
        public int AddedParameters, DuplicateParameters, ConflictCount, TotalParameters;
        public List<string> AddedSheets = new List<string>();
        public List<string> Conflicts = new List<string>();
        public List<string> Errors = new List<string>();
        public List<ImportIssue> ReferenceIssues = new List<ImportIssue>();
    }

    public sealed class CatalogMatchingReplacement
    {
        public bool CanApply;
        public int PreviousParameters, ImportedParameters, TotalParameters;
        public List<string> ReplacedSheets = new List<string>();
        public List<string> RetainedSheets = new List<string>();
        public List<string> SkippedSheets = new List<string>();
        public List<string> Errors = new List<string>();
        public List<ImportIssue> ReferenceIssues = new List<ImportIssue>();
    }

    public static class DecoderImport
    {
        private static string Key(Parameter parameter) { return parameter.Group + "\0" + parameter.Code; }
        private static IEnumerable<string> Names(TelemetryCatalog catalog)
        { return catalog.Sheets.Select(s => s.Name).Concat(catalog.Parameters.Select(p => p.Group)).Distinct(); }
        private static IEnumerable<string> Sources(TelemetryCatalog catalog)
        { return catalog.SourceFiles != null && catalog.SourceFiles.Count > 0 ? catalog.SourceFiles : new[] { catalog.SourceFile }.Where(s => !String.IsNullOrWhiteSpace(s)); }

        // Source row/index and runtime packet fields are provenance, not decoding rules.
        private static string Definition(Parameter p)
        {
            return CanonicalJson.Encode(new { p.Name, p.Group, p.Code, p.Offset, p.Length, p.BitStart, p.BitCount,
                p.Type, p.Scale, p.Bias, unit = p.Unit ?? "", p.Precision, format = p.Format ?? "", note = p.Note ?? "", p.Supported, diagnostic = p.Diagnostic ?? "", p.CrossByte,
                enums = p.Enums.OrderBy(e => e.Key,StringComparer.Ordinal).Select(e => new { e.Key, e.Value }).ToArray(), ranges = p.EnumRanges });
        }

        public static DecoderConfiguration Replace(DecoderConfiguration current, TelemetryCatalog incoming)
        {
            DecoderConfiguration result = current.Copy(); result.Catalog = incoming.Copy();
            var groups = new HashSet<string>(result.Catalog.Parameters.Select(p => p.Group));
            foreach (PacketDefinition packet in result.Packets)
            {
                packet.CrossByteOrder = result.Catalog.CrossByteOrder;
                if (packet.Groups.Any(g => !groups.Contains(g)))
                {
                    packet.Enabled = false;
                    result.Catalog.Issues.Add(new ImportIssue { Level = "warning", Sheet = "", Row = 0, Code = packet.Id,
                        Message = "包“" + packet.Name + "”引用的工作表不存在或已无可导入参数，替换时将停用该包，请重新关联参数表" });
                }
            }
            result.Validate(); return result;
        }

        public static DecoderConfiguration ReplaceMatching(DecoderConfiguration current, TelemetryCatalog incoming, out CatalogMatchingReplacement info)
        {
            info = new CatalogMatchingReplacement();
            DecoderConfiguration result = current.Copy(); TelemetryCatalog target = result.Catalog;
            var existingNames = new HashSet<string>(Names(target), StringComparer.Ordinal);
            var importedNames = new HashSet<string>(Names(incoming), StringComparer.Ordinal);
            info.ReplacedSheets = Names(incoming).Where(existingNames.Contains).ToList();
            info.SkippedSheets = Names(incoming).Where(n => !existingNames.Contains(n)).ToList();
            info.RetainedSheets = Names(target).Where(n => !importedNames.Contains(n)).ToList();
            info.TotalParameters = target.Parameters.Count;
            var matched = new HashSet<string>(info.ReplacedSheets, StringComparer.Ordinal);
            if (matched.Count == 0) info.Errors.Add("没有与当前配置同名的解码表，未进行替换；如需导入新表，请选择添加");
            if (target.CrossByteOrder != incoming.CrossByteOrder)
                info.Errors.Add("当前配置与新表的跨字节位序不同，无法仅替换同名表；请核对位序后重新读取");
            var previous = target.Parameters.Where(p => matched.Contains(p.Group)).ToList();
            var imported = incoming.Parameters.Where(p => matched.Contains(p.Group)).ToList();
            info.PreviousParameters = previous.Count; info.ImportedParameters = imported.Count;
            if (previous.GroupBy(Key).Any(g => g.Count() > 1) || imported.GroupBy(Key).Any(g => g.Count() > 1))
                info.Errors.Add("同名解码表中存在重复参数代号，请整理后重新读取");
            foreach (SheetSummary sheet in incoming.Sheets.Where(s => matched.Contains(s.Name) && s.HeaderRow == 0))
                info.Errors.Add("同名工作表“" + sheet.Name + "”未找到参数表头，不能替换已有表");
            if (info.Errors.Count > 0) return null;

            var byKey = previous.ToDictionary(Key);
            var replacements = new List<Parameter>();
            foreach (Parameter source in imported)
            {
                Parameter parameter = source.CopyForMapping(), old;
                // Keep stable bindings for parameters retained within the same table.
                if (byKey.TryGetValue(Key(source), out old)) { parameter.Id = old.Id; parameter.SourceId = old.SourceId; }
                replacements.Add(parameter);
            }
            var inserted = new HashSet<string>(); var parameters = new List<Parameter>();
            foreach (Parameter parameter in target.Parameters)
            {
                if (!matched.Contains(parameter.Group)) parameters.Add(parameter);
                else if (inserted.Add(parameter.Group)) parameters.AddRange(replacements.Where(p => p.Group == parameter.Group));
            }
            foreach (string name in info.ReplacedSheets.Where(n => !inserted.Contains(n))) parameters.AddRange(replacements.Where(p => p.Group == name));
            target.Parameters = parameters;
            info.TotalParameters = parameters.Count;
            if (parameters.GroupBy(p => p.Id).Any(g => g.Count() > 1)) info.Errors.Add("替换后的参数标识重复，请核对已有表与导入表");
            if (parameters.Count == 0 || parameters.Count > 20000) info.Errors.Add("替换后参数总数必须在1至20000项之间");
            if (info.Errors.Count > 0) return null;
            foreach (string name in info.ReplacedSheets)
            {
                SheetSummary source = incoming.Sheets.FirstOrDefault(s => s.Name == name);
                int index = target.Sheets.FindIndex(s => s.Name == name);
                SheetSummary sheet = source == null ? new SheetSummary { Name = name } : source.Copy();
                sheet.Parameters = parameters.Count(p => p.Group == name); sheet.Supported = parameters.Count(p => p.Group == name && p.Supported);
                if (index >= 0) target.Sheets[index] = sheet; else target.Sheets.Add(sheet);
            }
            target.Issues.RemoveAll(i => matched.Contains(i.Sheet));
            target.Issues.AddRange(incoming.Issues.Where(i => matched.Contains(i.Sheet)).Select(i => i.Copy()));
            target.Assumptions = target.Assumptions.Concat(incoming.Assumptions).Distinct().ToList();
            target.SourceFiles = Sources(target).Concat(Sources(incoming)).Distinct().ToList();
            target.SourceFile = String.Join("；", target.SourceFiles); target.GeneratedAt = incoming.GeneratedAt;
            var groups = new HashSet<string>(parameters.Select(p => p.Group));
            foreach (PacketDefinition packet in result.Packets.Where(p => p.Enabled && p.Groups.Any(g => matched.Contains(g) && !groups.Contains(g))))
            {
                packet.Enabled = false;
                target.Issues.Add(new ImportIssue { Level = "warning", Code = packet.Id,
                    Message = "包“" + packet.Name + "”引用的同名表已无可导入参数，替换时将停用该包，请重新关联参数表" });
            }
            result.Validate(); info.ReferenceIssues = target.ReferenceIssues;
            info.CanApply = true; return result;
        }

        public static DecoderConfiguration Add(DecoderConfiguration current, TelemetryCatalog incoming, out CatalogAddition info)
        {
            info = new CatalogAddition();
            DecoderConfiguration result = current.Copy(); TelemetryCatalog target = result.Catalog;
            var names = new HashSet<string>(Names(target));
            info.AddedSheets = Names(incoming).Where(n => !names.Contains(n)).ToList();
            if (target.CrossByteOrder != incoming.CrossByteOrder)
                info.Errors.Add("当前配置与新表的跨字节位序不同，无法直接添加；请核对位序后重新读取");

            var byKey = new Dictionary<string, Parameter>();
            var byId = new Dictionary<string, Parameter>();
            foreach (Parameter p in target.Parameters)
            {
                if (byKey.ContainsKey(Key(p))) info.Errors.Add("当前工作表“" + p.Group + "”存在重复编码“" + p.Code + "”，请先整理当前配置");
                else byKey[Key(p)] = p;
                byId[p.Id] = p;
            }
            foreach (Parameter p in incoming.Parameters)
            {
                Parameter existing;
                string conflict = null;
                if (byKey.TryGetValue(Key(p), out existing))
                {
                    if (Definition(existing) == Definition(p)) { info.DuplicateParameters++; continue; }
                    conflict = "工作表“" + p.Group + "”参数“" + p.Code + "”与已有定义不同，添加不会覆盖原定义";
                }
                else if (byId.TryGetValue(p.Id, out existing))
                    conflict = "新参数“" + p.Group + "/" + p.Code + "”的标识与已有参数“" + existing.Group + "/" + existing.Code + "”重复";
                if (conflict != null)
                {
                    info.ConflictCount++;
                    if (info.Conflicts.Count < 100) info.Conflicts.Add(conflict);
                    continue;
                }
                Parameter added = p.CopyForMapping(); target.Parameters.Add(added); byKey[Key(added)] = added; byId[added.Id] = added;
                info.AddedParameters++;
            }
            info.TotalParameters = target.Parameters.Count;
            info.ReferenceIssues = target.ReferenceIssues;
            if (target.Parameters.Count > 20000) info.Errors.Add("添加后参数超过20000项，无法保存");
            if (Names(target).Concat(Names(incoming)).Distinct().Count() > 128) info.Errors.Add("添加后参数表超过128张，无法保存");
            if (info.ConflictCount > 0 || info.Errors.Count > 0) return null;

            foreach (SheetSummary source in incoming.Sheets)
            {
                SheetSummary sheet = target.Sheets.FirstOrDefault(s => s.Name == source.Name);
                if (sheet == null) target.Sheets.Add(source.Copy());
                else
                {
                    // Overlapping tables may repeat reserved rows; do not count the same import twice.
                    sheet.Skipped = Math.Max(sheet.Skipped, source.Skipped);
                    sheet.ReservedSkipped = Math.Max(sheet.ReservedSkipped, source.ReservedSkipped);
                }
            }
            foreach (string name in target.Parameters.Select(p => p.Group).Distinct())
                if (!target.Sheets.Any(s => s.Name == name)) target.Sheets.Add(new SheetSummary { Name = name });
            foreach (SheetSummary sheet in target.Sheets)
            {
                Parameter[] parameters = target.Parameters.Where(p => p.Group == sheet.Name).ToArray();
                sheet.Parameters = parameters.Length; sheet.Supported = parameters.Count(p => p.Supported);
            }
            var knownIssues = new HashSet<string>(target.Issues.Select(i => CanonicalJson.Encode(i)));
            foreach (ImportIssue issue in incoming.Issues)
                if (knownIssues.Add(CanonicalJson.Encode(issue))) target.Issues.Add(issue.Copy());
            target.Assumptions = target.Assumptions.Concat(incoming.Assumptions).Distinct().ToList();
            target.SourceFiles = Sources(target).Concat(Sources(incoming)).Distinct().ToList();
            target.SourceFile = String.Join("；", target.SourceFiles);
            target.GeneratedAt = incoming.GeneratedAt;
            result.Validate();
            if (info.AddedParameters == 0 && info.AddedSheets.Count == 0)
            { info.Errors.Add("没有可添加的新参数或工作表，相同参数已全部存在，无需重复保存"); return null; }
            info.CanApply = true; return result;
        }
    }
}
