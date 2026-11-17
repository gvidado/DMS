using System;
using System.Net;
using System.Reflection;

namespace TelemetryMonitor
{
    public sealed partial class Server
    {
        private bool HandleDshPlugin(HttpListenerContext ctx, Session session)
        {
            string path = ctx.Request.Url.AbsolutePath;
            if (!path.StartsWith("/api/plugin/v1/", StringComparison.Ordinal)) return false;
            if (ctx.Request.HttpMethod != "GET")
            {
                Json(ctx.Response, new { error = "DSH兼容接口仅支持读取" }, 405);
                return true;
            }
            if (path == "/api/plugin/v1/info")
            {
                Assembly assembly = Assembly.GetExecutingAssembly();
                AssemblyInformationalVersionAttribute info = (AssemblyInformationalVersionAttribute)Attribute.GetCustomAttribute(assembly, typeof(AssemblyInformationalVersionAttribute));
                string version = info == null ? assembly.GetName().Version.ToString() : info.InformationalVersion;
                string origin = ctx.Request.Url.GetLeftPart(UriPartial.Authority);
                Json(ctx.Response, new
                {
                    apiVersion = "1.0",
                    systemVersion = version,
                    product = "数据判读系统",
                    consoleUrl = origin + "/",
                    serverTime = BeijingTime.Format(DateTime.UtcNow),
                    authenticatedUser = session.Username,
                    completeUi = true,
                    uiMode = "dsh-embedded-browser",
                    capabilities = new string[]
                    {
                        "complete-console", "live-state", "decoder", "parameter-display",
                        "alarms", "abnormal-records", "continuous-judgment", "sequence",
                        "command-library", "command-send", "capture", "replay",
                        "network-replay", "archives", "configuration", "user-management"
                    }
                }, 200);
                return true;
            }
            Json(ctx.Response, new { error = "DSH兼容接口不存在" }, 404);
            return true;
        }
    }
}
