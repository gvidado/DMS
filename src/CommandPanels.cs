using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace TelemetryMonitor
{
    public sealed class CommandPanelConfiguration
    {
        public string Revision = "initial";
        public List<CommandPanelScreen> Screens = new List<CommandPanelScreen>();
        internal void Validate()
        {
            if (Screens == null || Screens.Count < 1 || Screens.Count > 20) throw new ArgumentException("请配置1～20个指令画面");
            var ids = new HashSet<string>();
            foreach (var screen in Screens)
            {
                if (screen == null || !Key(screen.Id) || !ids.Add(screen.Id)) throw new ArgumentException("指令画面编号无效或重复");
                screen.Name = Label(screen.Name, 40, "画面名称");
                if (screen.Buttons == null || screen.Buttons.Count > 1000) throw new ArgumentException("每个画面最多1000个指令按钮");
                var buttons = new HashSet<string>();
                foreach (var button in screen.Buttons)
                {
                    if (button == null || !Key(button.Id) || !buttons.Add(button.Id)) throw new ArgumentException("指令按钮编号无效或重复");
                    button.Label = (button.Label ?? "").Trim();
                    if (button.Label.Length > 80) throw new ArgumentException("按钮文字最多80字");
                    if (button.Type != "button" && button.Type != "sender") throw new ArgumentException("指令面板控件类型无效");
                    int minimumWidth = button.Type == "sender" ? 440 : 80, minimumHeight = button.Type == "sender" ? 300 : 44;
                    if (!Finite(button.X, 0, 10000) || !Finite(button.Y, 0, 10000) || !Finite(button.Width, minimumWidth, 10000) || !Finite(button.Height, minimumHeight, 10000) || !Finite(button.FontSize, 12, 40)) throw new ArgumentException("控件尺寸至少" + minimumWidth + "×" + minimumHeight + "像素，字号12～40，位置及尺寸不超过10000像素");
                    if (!new[] { "teal", "blue", "amber", "slate" }.Contains(button.Theme)) throw new ArgumentException("按钮颜色无效");
                    if (button.CommandNumber < 0 || button.CommandNumber > 9007199254740991L) throw new ArgumentException("指令记录号无效");
                    button.CommandCode = (button.CommandCode ?? "").Trim().ToUpperInvariant();
                    button.CommandKind = button.CommandKind ?? "";
                    button.CommandIdentity = button.CommandIdentity ?? "";
                    if (button.CommandCode.Length > 64 || button.CommandKind.Length > 16 || (button.CommandIdentity.Length > 0 && !Regex.IsMatch(button.CommandIdentity, "^[0-9A-F]{64}$"))) throw new ArgumentException("指令绑定格式无效");
                    if (button.Type == "sender" && (button.CommandNumber != 0 || button.CommandCode.Length > 0 || button.CommandKind.Length > 0 || button.CommandIdentity.Length > 0)) throw new ArgumentException("指令发送框不固定关联单条指令，请在框内搜索后选择");
                }
            }
        }
        private static bool Key(string text) { return text != null && Regex.IsMatch(text, "^[A-Za-z0-9_-]{1,64}$"); }
        private static bool Finite(double value, double min, double max) { return !Double.IsNaN(value) && !Double.IsInfinity(value) && value >= min && value <= max; }
        private static string Label(string text, int max, string name) { text = (text ?? "").Trim(); if (text.Length < 1 || text.Length > max) throw new ArgumentException(name + "须为1～" + max + "字"); return text; }
    }
    public sealed class CommandPanelScreen
    {
        public string Id, Name;
        public List<CommandPanelButton> Buttons = new List<CommandPanelButton>();
    }
    public sealed class CommandPanelButton
    {
        public string Id, Label = "", Theme = "teal", Type = "button";
        public double X = 24, Y = 24, Width = 200, Height = 76, FontSize = 18;
        public long CommandNumber;
        public string CommandCode = "", CommandKind = "", CommandIdentity = "";
    }
    public sealed class CommandPanelBinding
    {
        public string screenId, buttonId, code = "", name = "", kind = "", error = "";
        public long number;
        public string[] destinations = new string[0];
        public bool available;
    }
    public sealed partial class Auth
    {
        public CommandPanelConfiguration CommandPanels(Session session)
        {
            lock (Gate)
            {
                var value = User(session).CommandPanelLayout;
                if (value == null) value = new CommandPanelConfiguration { Screens = new List<CommandPanelScreen> { new CommandPanelScreen { Id = "screen1", Name = "画面1" } } };
                return JsonFile.Decode<CommandPanelConfiguration>(JsonFile.Encode(value));
            }
        }
        public CommandPanelConfiguration SaveCommandPanels(Session session, CommandPanelConfiguration incoming)
        {
            incoming.Validate();
            lock (Gate)
            {
                var user = User(session); var previous = user.CommandPanelLayout;
                if (incoming.Revision != (previous == null ? "initial" : previous.Revision)) throw new InvalidOperationException("指令面板已被其他页面修改，请重新载入后保存");
                var value = JsonFile.Decode<CommandPanelConfiguration>(JsonFile.Encode(incoming)); value.Revision = Guid.NewGuid().ToString("N");
                user.CommandPanelLayout = value;
                try { Save(); } catch { user.CommandPanelLayout = previous; throw; }
                return JsonFile.Decode<CommandPanelConfiguration>(JsonFile.Encode(value));
            }
        }
    }
    public sealed partial class ObservedCommands
    {
        private long panelIdentityRevision = -1;
        private readonly Dictionary<long, string> panelIdentities = new Dictionary<long, string>();
        private string PanelIdentity(ObservedCommand entry)
        {
            if (panelIdentityRevision != revision) { panelIdentities.Clear(); panelIdentityRevision = revision; }
            string identity;
            if (!panelIdentities.TryGetValue(entry.Number, out identity))
            {
                string content = CommandEnvelope.IdentityHex(entry.Length == 0 ? new byte[0] : PacketCodec.Hex(entry.PayloadHex));
                using (var hash = SHA256.Create()) identity = BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(content))).Replace("-", "");
                panelIdentities[entry.Number] = identity;
            }
            return identity;
        }
        internal CommandPanelBinding ResolvePanelButton(CommandPanelButton button, bool initialize)
        {
            lock (gate)
            {
                var result = new CommandPanelBinding { buttonId = button.Id };
                if (!ready) { result.error = error; return result; }
                if (button.CommandNumber == 0) { result.error = "未关联指令"; return result; }
                var entry = configuration.Commands.FirstOrDefault(c => c.MatchesNumber(button.CommandNumber));
                if (entry == null) { result.error = "关联指令已不在库中，请重新选择"; return result; }
                string identity = PanelIdentity(entry);
                if (button.CommandIdentity.Length > 0 && button.CommandIdentity != identity) { result.error = "关联指令内容已变化，请重新选择"; return result; }
                var category = button.CommandIdentity.Length > 0 ? entry.Categories.FirstOrDefault(c => c.Kind == button.CommandKind) : entry.Categories.FirstOrDefault(c => c.Code == button.CommandCode);
                bool unclassified = button.CommandIdentity.Length > 0 ? button.CommandKind == "" : button.CommandCode.Length == 0;
                if (category == null && !unclassified) { result.error = "指令类别已变化，请重新选择"; return result; }
                result.destinations = unclassified ? entry.DestinationIPs.Where(ip => !entry.Categories.Any(c => c.DestinationIPs.Contains(ip))).ToArray() : category.DestinationIPs.ToArray();
                if (result.destinations.Length == 0) { result.error = "所选指令无有效目的IP"; return result; }
                result.number = entry.Number; result.code = unclassified ? "" : category.Code; result.kind = unclassified ? "未分类" : category.Kind; result.name = entry.Name; result.available = true;
                if (initialize) { button.CommandNumber = entry.Number; button.CommandCode = result.code; button.CommandKind = unclassified ? "" : category.Kind; button.CommandIdentity = identity; }
                return result;
            }
        }
    }
    public sealed partial class Server
    {
        private object CommandPanelResponse(CommandPanelConfiguration configuration)
        {
            var bindings = new List<CommandPanelBinding>();
            foreach (var screen in configuration.Screens) foreach (var button in screen.Buttons)
            {
                var binding = monitor.Outgoing.Commands.ResolvePanelButton(button, false); binding.screenId = screen.Id; bindings.Add(binding);
            }
            return new { configuration, bindings, libraryRevision = monitor.Outgoing.Commands.DisplayRevision };
        }
        private bool HandleCommandPanels(HttpListenerContext ctx, Session session)
        {
            string path = ctx.Request.Url.AbsolutePath;
            if (path != "/api/command-panels" && path != "/api/command-panels/preview" && path != "/api/command-panels/bind") return false;
            if (path == "/api/command-panels/bind" && ctx.Request.HttpMethod == "POST")
            {
                var body = Body(ctx.Request); long number;
                if (!Int64.TryParse(Text(body, "number"), out number) || number < 1) throw new ArgumentException("请选择库内指令");
                lock (commandGate)
                {
                    var button = new CommandPanelButton { CommandNumber = number, CommandCode = Text(body, "code") };
                    var binding = monitor.Outgoing.Commands.ResolvePanelButton(button, true);
                    if (!binding.available) throw new ArgumentException(binding.error);
                    Json(ctx.Response, new { button, binding }, 200);
                }
                return true;
            }
            if (path == "/api/command-panels" && ctx.Request.HttpMethod == "GET")
            { lock (commandGate) Json(ctx.Response, CommandPanelResponse(auth.CommandPanels(session)), 200); return true; }
            if (path == "/api/command-panels" && ctx.Request.HttpMethod == "POST")
            {
                var body = Body(ctx.Request, 8 * 1024 * 1024); object input;
                if (!body.TryGetValue("configuration", out input)) throw new ArgumentException("缺少指令面板配置");
                var configuration = JsonFile.Decode<CommandPanelConfiguration>(JsonFile.Encode(input));
                if (configuration == null) throw new ArgumentException("缺少指令面板配置"); configuration.Validate();
                lock (commandGate)
                {
                    foreach (var screen in configuration.Screens) foreach (var button in screen.Buttons)
                    {
                        bool fresh = button.CommandNumber > 0 && button.CommandIdentity.Length == 0;
                        var binding = monitor.Outgoing.Commands.ResolvePanelButton(button, true);
                        if (fresh && !binding.available) throw new ArgumentException(screen.Name + "：" + binding.error);
                    }
                    Json(ctx.Response, CommandPanelResponse(auth.SaveCommandPanels(session, configuration)), 200);
                }
                return true;
            }
            if (path == "/api/command-panels/preview" && ctx.Request.HttpMethod == "POST")
            {
                var body = Body(ctx.Request);
                lock (commandGate)
                {
                    var configuration = auth.CommandPanels(session);
                    if (configuration.Revision != Text(body, "revision")) throw new InvalidOperationException("面板配置已变化，请刷新后再发送");
                    var screen = configuration.Screens.FirstOrDefault(s => s.Id == Text(body, "screenId"));
                    var button = screen == null ? null : screen.Buttons.FirstOrDefault(b => b.Id == Text(body, "buttonId"));
                    if (button == null) throw new ArgumentException("按钮已不存在，请刷新面板");
                    if (button.Type != "button") throw new ArgumentException("请在发送框中选择库内指令");
                    var binding = monitor.Outgoing.Commands.ResolvePanelButton(button, false);
                    if (!binding.available) throw new InvalidOperationException(binding.error);
                    Json(ctx.Response, commandTransmitter.Preview(session.Username, binding.number, binding.code, null), 200);
                }
                return true;
            }
            Json(ctx.Response, new { error = "指令面板接口不存在" }, 404); return true;
        }
    }
}
