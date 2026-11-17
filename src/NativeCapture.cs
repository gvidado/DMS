using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;

namespace TelemetryMonitor
{
    public sealed class CaptureAdapter
    {
        public string id, name;
        public string[] addresses;
        public bool loopback;
    }
    // Load only an installed system capture library, never a DLL from the working directory.
    internal static class NativeCapture
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibraryEx(string file, IntPtr unused, uint flags);
        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true)]
        private static extern IntPtr GetProcAddress(IntPtr module, string name);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int FindDelegate(out IntPtr devices, StringBuilder error);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void FreeDelegate(IntPtr devices);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Ansi)] private delegate IntPtr OpenDelegate(string name, int snap, int promisc, int timeout, StringBuilder error);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void CloseDelegate(IntPtr handle);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int LinkDelegate(IntPtr handle);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int NextDelegate(IntPtr handle, out IntPtr header, out IntPtr data);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int NonblockDelegate(IntPtr handle, int enabled, StringBuilder error);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Ansi)] private delegate int CompileDelegate(IntPtr handle, ref BpfProgram program, string filter, int optimize, uint mask);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int FilterDelegate(IntPtr handle, ref BpfProgram program);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void FreeCodeDelegate(ref BpfProgram program);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr ErrorDelegate(IntPtr handle);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int StatsDelegate(IntPtr handle, IntPtr stats);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int MinCopyDelegate(IntPtr handle, int bytes);
        [StructLayout(LayoutKind.Sequential)] private struct BpfProgram { public uint Length; public IntPtr Instructions; }
        [StructLayout(LayoutKind.Sequential)] private struct Device { public IntPtr Next, Name, Description, Addresses; public uint Flags; }
        [StructLayout(LayoutKind.Sequential)] private struct Address { public IntPtr Next, Value, Mask, Broadcast, Destination; }
        private static readonly object gate = new object();
        private static IntPtr library;
        private static FindDelegate find;
        private static FreeDelegate free;
        private static OpenDelegate open;
        private static NonblockDelegate nonblock;
        private static CompileDelegate compile;
        private static FilterDelegate filter;
        private static FreeCodeDelegate freeCode;
        private static ErrorDelegate getError;
        private static StatsDelegate stats;
        private static MinCopyDelegate minCopy;
        internal static CloseDelegate Close;
        internal static LinkDelegate Link;
        internal static NextDelegate Next;
        internal static string LibraryPath = "";
        private static T Function<T>(IntPtr module, string name) where T : class
        {
            IntPtr pointer = GetProcAddress(module, name);
            if (pointer == IntPtr.Zero) throw new InvalidOperationException("抓包驱动缺少接口：" + name + "，请修复Npcap安装后重启软件");
            return Marshal.GetDelegateForFunctionPointer(pointer, typeof(T)) as T;
        }
        internal static void Ensure()
        {
            lock (gate)
            {
                if (library != IntPtr.Zero) return;
                string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "Npcap", "wpcap.dll");
                if (!File.Exists(path)) path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "wpcap.dll");
                if (!File.Exists(path)) throw new InvalidOperationException("未安装Npcap抓包驱动。请在Windows 10服务器离线安装Npcap后重启软件；本次统一监听未启动");
                IntPtr module = LoadLibraryEx(path, IntPtr.Zero, 8);
                if (module == IntPtr.Zero) throw new InvalidOperationException("抓包库加载失败（Windows错误" + Marshal.GetLastWin32Error() + "）。请检查Npcap安装及程序位数");
                find = Function<FindDelegate>(module, "pcap_findalldevs"); free = Function<FreeDelegate>(module, "pcap_freealldevs");
                open = Function<OpenDelegate>(module, "pcap_open_live"); Close = Function<CloseDelegate>(module, "pcap_close");
                Link = Function<LinkDelegate>(module, "pcap_datalink"); Next = Function<NextDelegate>(module, "pcap_next_ex");
                nonblock = Function<NonblockDelegate>(module, "pcap_setnonblock"); compile = Function<CompileDelegate>(module, "pcap_compile");
                filter = Function<FilterDelegate>(module, "pcap_setfilter"); freeCode = Function<FreeCodeDelegate>(module, "pcap_freecode");
                getError = Function<ErrorDelegate>(module, "pcap_geterr"); stats = Function<StatsDelegate>(module, "pcap_stats");
                minCopy = Function<MinCopyDelegate>(module, "pcap_setmintocopy"); LibraryPath = path; library = module;
            }
        }
        internal static CaptureAdapter[] Adapters()
        {
            Ensure(); var result = new List<CaptureAdapter>(); var error = new StringBuilder(512); IntPtr head;
            if (find(out head, error) != 0) throw new InvalidOperationException("无法列出抓包网卡：" + error + "。请检查Npcap服务及管理员权限");
            try
            {
                var nics = NetworkInterface.GetAllNetworkInterfaces();
                for (IntPtr p = head; p != IntPtr.Zero; )
                {
                    Device device = (Device)Marshal.PtrToStructure(p, typeof(Device)); string id = Marshal.PtrToStringAnsi(device.Name) ?? "";
                    var addresses = new HashSet<string>();
                    for (IntPtr a = device.Addresses; a != IntPtr.Zero; )
                    {
                        Address item = (Address)Marshal.PtrToStructure(a, typeof(Address));
                        if (item.Value != IntPtr.Zero && Marshal.ReadInt16(item.Value) == 2)
                        { byte[] bytes = new byte[4]; Marshal.Copy(IntPtr.Add(item.Value, 4), bytes, 0, 4); addresses.Add(new IPAddress(bytes).ToString()); }
                        a = item.Next;
                    }
                    bool loop = id.IndexOf("loopback", StringComparison.OrdinalIgnoreCase) >= 0 || (device.Flags & 1) != 0;
                    NetworkInterface nic = nics.FirstOrDefault(n => id.IndexOf(n.Id.Trim('{', '}'), StringComparison.OrdinalIgnoreCase) >= 0);
                    if (nic != null) foreach (var ip in nic.GetIPProperties().UnicastAddresses.Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)) addresses.Add(ip.Address.ToString());
                    if (loop) addresses.Add("127.0.0.1");
                    if (addresses.Count > 0) result.Add(new CaptureAdapter { id = id, name = nic == null ? (Marshal.PtrToStringAnsi(device.Description) ?? id) : nic.Name, addresses = addresses.ToArray(), loopback = loop });
                    p = device.Next;
                }
            }
            finally { free(head); }
            return result.ToArray();
        }
        internal static IntPtr Open(string device, string expression)
        {
            Ensure(); var error = new StringBuilder(512); IntPtr handle = open(device, 262144, 0, 100, error);
            if (handle == IntPtr.Zero) throw new InvalidOperationException("抓包网卡打开失败：" + error + "。请检查Npcap服务、网卡和管理员权限");
            try
            {
                var program = new BpfProgram();
                if (compile(handle, ref program, expression, 1, UInt32.MaxValue) != 0) throw new InvalidOperationException("抓包过滤器创建失败：" + Error(handle));
                try { if (filter(handle, ref program) != 0) throw new InvalidOperationException("抓包过滤器应用失败：" + Error(handle)); }
                finally { freeCode(ref program); }
                if (nonblock(handle, 1, error) != 0) throw new InvalidOperationException("抓包非阻塞模式设置失败：" + error);
                if (minCopy(handle, 1) != 0) throw new InvalidOperationException("抓包即时读取设置失败：" + Error(handle));
                return handle;
            }
            catch { Close(handle); throw; }
        }
        internal static string Error(IntPtr handle) { return Marshal.PtrToStringAnsi(getError(handle)) ?? "未知抓包错误"; }
        internal static uint[] Statistics(IntPtr handle)
        {
            // Windows pcap_stat may append extra members; reserve enough space for either ABI.
            IntPtr buffer = Marshal.AllocHGlobal(256);
            try { if (stats(handle, buffer) != 0) return null; return new[] { unchecked((uint)Marshal.ReadInt32(buffer, 0)), unchecked((uint)Marshal.ReadInt32(buffer, 4)), unchecked((uint)Marshal.ReadInt32(buffer, 8)) }; }
            finally { Marshal.FreeHGlobal(buffer); }
        }
    }
}
