using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace TelemetryMonitor
{
    public sealed partial class Monitor
    {
        private struct SequenceHistoryLocation
        {
            public long Offset;
            public long Id;
        }

        private sealed class SequenceHistoryIndex
        {
            internal readonly Dictionary<string, List<SequenceHistoryLocation>> Runs = new Dictionary<string, List<SequenceHistoryLocation>>(StringComparer.Ordinal);
            internal readonly Dictionary<string, SequenceRunSummary> Summaries = new Dictionary<string, SequenceRunSummary>(StringComparer.Ordinal);
            internal readonly List<SequenceRunSummary> RunsByStart = new List<SequenceRunSummary>();
            internal readonly List<SequenceHistoryLocation> All = new List<SequenceHistoryLocation>();
            internal readonly List<SequenceHistoryLocation> Judge = new List<SequenceHistoryLocation>();
            internal readonly List<SequenceHistoryLocation> Execute = new List<SequenceHistoryLocation>();
            internal readonly List<SequenceHistoryLocation> Abnormal = new List<SequenceHistoryLocation>();
            internal long Position;
            internal DateTime LastWrite;
            internal Guid Generation = Guid.NewGuid();
        }

        private static readonly UTF8Encoding SequenceHistoryEncoding = new UTF8Encoding(false, true);
        private readonly object sequenceHistoryIndexGate = new object();
        private SequenceHistoryIndex sequenceHistoryIndex = new SequenceHistoryIndex();

        private static SequenceHistoryEvent ReadHistoryEvent(FileStream stream, long limit, MemoryStream bytes, long id)
        {
            bytes.SetLength(0);
            while (stream.Position < limit)
            {
                int next = stream.ReadByte();
                if (next < 0) break;
                if (next != 10)
                {
                    bytes.WriteByte((byte)next);
                    continue;
                }

                string line = SequenceHistoryEncoding.GetString(bytes.GetBuffer(), 0, (int)bytes.Length).TrimStart('\uFEFF').TrimEnd('\r');
                bytes.SetLength(0);
                if (String.IsNullOrWhiteSpace(line)) continue;
                var entry = JsonFile.Decode<SequenceHistoryEvent>(line);
                if (entry == null || String.IsNullOrEmpty(entry.RunId)) throw new InvalidDataException("序列历史记录格式无效");
                entry.HistoryId = id;
                return entry;
            }

            return null;
        }

        // Call only while holding sequenceHistoryIndexGate. Index complete, append-only records by byte offset.
        private SequenceHistoryIndex UpdateSequenceHistoryIndex()
        {
            var file = new FileInfo(SequenceHistoryPath);
            var index = sequenceHistoryIndex;
            if (!file.Exists)
            {
                if (index.Position != 0) sequenceHistoryIndex = new SequenceHistoryIndex();
                return sequenceHistoryIndex;
            }
            long length = file.Length;
            DateTime modified = file.LastWriteTimeUtc;
            if (length == index.Position && modified == index.LastWrite) return index;
            if (length <= index.Position) index = sequenceHistoryIndex = new SequenceHistoryIndex();

            using (var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var bytes = new MemoryStream())
            {
                stream.Position = index.Position;
                while (stream.Position < length)
                {
                    var location = new SequenceHistoryLocation { Offset = stream.Position, Id = index.All.Count + 1L };
                    var entry = ReadHistoryEvent(stream, length, bytes, location.Id);
                    if (entry == null) break;

                    List<SequenceHistoryLocation> run;
                    if (!index.Runs.TryGetValue(entry.RunId, out run)) index.Runs.Add(entry.RunId, run = new List<SequenceHistoryLocation>());
                    run.Add(location);
                    index.All.Add(location);
                    if (entry.Mode == "judge") index.Judge.Add(location);
                    if (entry.Mode == "execute") index.Execute.Add(location);
                    if (entry.Event == "异常") index.Abnormal.Add(location);
                    UpdateRunSummary(index, entry);
                    index.Position = stream.Position;
                }
            }

            index.LastWrite = modified;
            return index;
        }

        private static void UpdateRunSummary(SequenceHistoryIndex index, SequenceHistoryEvent entry)
        {
            SequenceRunSummary summary;
            if (!index.Summaries.TryGetValue(entry.RunId, out summary))
            {
                summary = new SequenceRunSummary { RunId = entry.RunId, Code = entry.Code, Name = entry.Sequence,
                    Mode = entry.Mode, StartedAt = entry.Time, Order = index.Summaries.Count };
                index.Summaries.Add(entry.RunId, summary);
                index.RunsByStart.Add(summary);
            }
            if (entry.Event == "开始")
            {
                summary.StartedAt = entry.Time;
                summary.Name = entry.Sequence;
                summary.Mode = entry.Mode;
            }
            if (!String.IsNullOrEmpty(entry.Code)) summary.Code = entry.Code;
            summary.Events++;
            summary.LastTime = entry.Time;
            summary.Status = entry.Event == "步骤结果" ? "运行中" : entry.Status;
        }

        private IEnumerable<SequenceHistoryEvent> ReadHistoryLocations(SequenceHistoryLocation[] locations)
        {
            if (locations.Length == 0) yield break;
            using (var stream = new FileStream(SequenceHistoryPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var bytes = new MemoryStream())
            {
                long length = stream.Length;
                foreach (var location in locations)
                {
                    stream.Position = location.Offset;
                    var entry = ReadHistoryEvent(stream, length, bytes, location.Id);
                    if (entry == null) throw new InvalidDataException("序列历史索引已失效，请重新查询");
                    yield return entry;
                }
            }
        }
    }
}
