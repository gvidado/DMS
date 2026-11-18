using System;
using System.IO;
using System.Text;
using System.Threading;

namespace TelemetryMonitor
{
    public static class Program
    {
        public static int Main(string[] args)
        {
            // Keep the active console code page on legacy Windows consoles.
            // Redirected text is UTF-8; interactive startup text below is ASCII.
            if (Console.IsOutputRedirected) Console.OutputEncoding = new UTF8Encoding(false);
            if (args.Length > 0 && args[0] == "--generate-decoder")
            {
                try
                {
                    if (args.Length != 4 || args[2] != "--output") throw new ArgumentException("用法：TelemetryMonitor.exe --generate-decoder 遥测格式.xlsx --output 解码配置.json");
                    var generated = new DecoderConfiguration { Catalog = WorkbookReader.Read(File.ReadAllBytes(args[1]), args[1]) };
                    generated.Validate(); JsonFile.Save(Path.GetFullPath(args[3]), generated);
                    Console.WriteLine("已生成解码配置：" + Path.GetFullPath(args[3]));
                    Console.WriteLine("参数" + generated.Catalog.Parameters.Count + "，可识别" + generated.Catalog.Parameters.FindAll(p => p.Supported).Count + "，问题提示" + generated.Catalog.Issues.Count);
                    return 0;
                }
                catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 1; }
            }
            string root = AppDomain.CurrentDomain.BaseDirectory; bool local = false; int port = 0;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--root" && i + 1 < args.Length) root = Path.GetFullPath(args[++i]);
                else if (args[i] == "--local") local = true;
                else if (args[i] == "--web-port" && i + 1 < args.Length) port = Int32.Parse(args[++i]);
                else if (args[i] == "--self-test") return SelfTests.Run();
                else { Console.Error.WriteLine("未知参数：" + args[i]); return 2; }
            }
            try
            {
                try{DocumentationOrganizer.Organize(root);}catch(IOException ex){Console.WriteLine("Documentation organization skipped: "+ex.Message);}catch(UnauthorizedAccessException ex){Console.WriteLine("Documentation organization skipped: "+ex.Message);}
                using (var server = new Server(root, local, port))
                using (var quit = new ManualResetEvent(false))
                {
                    server.Start();
                    int effectivePort = port > 0 ? port : JsonFile.Load<Settings>(Path.Combine(root, "config", "server.json")).WebPort;
                    Console.WriteLine("Telemetry Monitor server v0.10.32");
                    Console.WriteLine("Local browser: http://localhost:" + effectivePort);
                    Console.WriteLine(local ? "Access: local preview only." : "LAN browser: http://<SERVER-IP>:" + effectivePort);
                    Console.WriteLine("On first use, open the local browser to create an administrator account.");
                    Console.WriteLine("Start UDP reception from the browser page.");
                    Console.WriteLine("Keep this window open. Close it or press Ctrl+C to stop the server.");
                    Console.CancelKeyPress += delegate(object sender, ConsoleCancelEventArgs e) { e.Cancel = true; quit.Set(); };
                    quit.WaitOne();
                }
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("Startup failed: " + ex.Message);
                Console.Error.WriteLine("If access is denied, run the LAN launcher as Administrator or configure an HTTP URL reservation.");
                return 1;
            }
        }
    }
}
