using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace TelemetryMonitor
{
    internal sealed class EventStamp
    {
        [DllImport("kernel32.dll")]private static extern void GetSystemTimePreciseAsFileTime(out long time);
        internal static DateTime PreciseUtcNow(){try{long time;GetSystemTimePreciseAsFileTime(out time);return DateTime.FromFileTimeUtc(time);}catch(EntryPointNotFoundException){return DateTime.UtcNow;}}
        internal static readonly DateTime ServerClockOrigin;
        internal static readonly long ServerCounterOrigin;
        static EventStamp()
        {
            long before=Stopwatch.GetTimestamp();
            ServerClockOrigin=PreciseUtcNow();
            long after=Stopwatch.GetTimestamp();
            ServerCounterOrigin=before+(after-before)/2;
        }
        internal static DateTime ServerTime(long counter)
        {
            long elapsed=counter-ServerCounterOrigin;
            long ticks=(elapsed/Stopwatch.Frequency)*TimeSpan.TicksPerSecond+
                (elapsed%Stopwatch.Frequency)*TimeSpan.TicksPerSecond/Stopwatch.Frequency;
            return ServerClockOrigin.AddTicks(ticks);
        }
        internal DateTime ReceivedUtc,ObservedUtc;
        internal long Monotonic;
        internal double DataSeconds;
        internal string Basis;
        internal static EventStamp Now(DateTime? captured=null,string basis=null)
        {return At(Stopwatch.GetTimestamp(),captured,basis);}
        internal static EventStamp At(long monotonic,DateTime? captured=null,string basis=null)
        {
            DateTime received=ServerTime(monotonic);
            return new EventStamp{ReceivedUtc=received,ObservedUtc=captured.HasValue?captured.Value.ToUniversalTime():received,
                Monotonic=monotonic,DataSeconds=monotonic/(double)Stopwatch.Frequency,Basis=basis??(captured.HasValue?"CAPTURE_TIME":"SOCKET_RECEIVE")};
        }
    }
}
