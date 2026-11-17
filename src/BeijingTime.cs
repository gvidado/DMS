using System;
using System.Globalization;

namespace TelemetryMonitor
{
    // Display and exported instants always use UTC+08:00, independent of the browser/server zone.
    // Capture ordering, freshness checks and timeouts retain their original monotonic/UTC clocks.
    internal static class BeijingTime
    {
        private static readonly TimeSpan Offset=TimeSpan.FromHours(8);
        internal static DateTime Now { get { return At(DateTime.UtcNow); } }
        internal static DateTime At(DateTime value)
        {
            if(value.Kind==DateTimeKind.Unspecified)return value;
            return DateTime.SpecifyKind(value.ToUniversalTime().Add(Offset),DateTimeKind.Unspecified);
        }
        internal static string Format(DateTime value,string format="yyyy-MM-dd HH:mm:ss.fff")
        {return At(value).ToString(format,CultureInfo.InvariantCulture);}
        internal static string NowText(){return Format(DateTime.UtcNow);}
        internal static string Iso(DateTime value)
        {return new DateTimeOffset(At(value),Offset).ToString("o",CultureInfo.InvariantCulture);}
    }
}
