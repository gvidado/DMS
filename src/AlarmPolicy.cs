using System;

namespace TelemetryMonitor
{
    internal static class AlarmPolicy
    {
        // Old journals retain their original IDs and acknowledgements. Only their alarm view is filtered.
        internal static bool IsVisible(AlarmEntry entry)
        {
            if (entry.Level != "warn" && entry.Level != "error") return false;
            string message = entry.Message ?? "";
            if (entry.Category == "SPP异常" && entry.Packet == "未识别" && entry.Apid < 0) return false;
            if (message.StartsWith("无匹配的SPP包标识", StringComparison.Ordinal) || message.StartsWith("SPP包标识或APID不匹配", StringComparison.Ordinal)) return false;
            if (message.StartsWith("SPP字段未匹配", StringComparison.Ordinal) && message.IndexOf("副导头不完整", StringComparison.Ordinal) < 0) return false;
            if (entry.Category == "SPP异常" && entry.Packet == "未匹配SPP" &&
                (message.StartsWith("源地址不符", StringComparison.Ordinal) || message.StartsWith("业务类型不符", StringComparison.Ordinal) || message.StartsWith("业务子类型不符", StringComparison.Ordinal))) return false;
            return true;
        }
    }
}
