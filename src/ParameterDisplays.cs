using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;

namespace TelemetryMonitor
{
    public sealed class ParameterDisplayConfiguration
    {
        public string Revision = "initial";
        public List<ParameterDisplayScreen> Screens = new List<ParameterDisplayScreen>();
        public void Validate()
        {
            if (Screens == null || Screens.Count < 1 || Screens.Count > 20) throw new ArgumentException("请配置1～20个显示画面");
            var screenIds = new HashSet<string>();
            foreach (var screen in Screens)
            {
                if (screen == null || !Key(screen.Id) || !screenIds.Add(screen.Id)) throw new ArgumentException("画面编号无效或重复");
                screen.Name = Name(screen.Name, "画面名称");
                if (screen.Boxes == null || screen.Boxes.Count > 40) throw new ArgumentException("每个画面最多40个参数框");
                var boxIds = new HashSet<string>();
                foreach (var box in screen.Boxes)
                {
                    if (box == null || !Key(box.Id) || !boxIds.Add(box.Id)) throw new ArgumentException("参数框编号无效或重复");
                    box.Name = Name(box.Name, "参数框名称");
                    if (!Finite(box.X, 0, 10000) || !Finite(box.Y, 0, 10000) || !Finite(box.Width, 300, 10000) || !Finite(box.Height, 180, 10000)) throw new ArgumentException("参数框位置或尺寸无效，最小为300×180像素");
                    if (!Finite(box.FontSize, 12, 40) || !Finite(box.RowHeight, 32, 120) || !Finite(box.NameWidth, 80, 500) || !Finite(box.ValueWidth, 70, 500) || box.Columns < 1 || box.Columns > 6 || (box.Order != "row" && box.Order != "column")) throw new ArgumentException("参数框显示样式超出允许范围");
                    if (box.Parameters == null || box.Parameters.Any(id => String.IsNullOrWhiteSpace(id) || id.Length > 512)) throw new ArgumentException("参数列表格式无效，参数标识须为1～512个字符");
                    box.Parameters = box.Parameters.Distinct().ToList();
                }
            }
        }
        private static bool Key(string text) { return text != null && System.Text.RegularExpressions.Regex.IsMatch(text, "^[A-Za-z0-9_-]{1,64}$"); }
        private static bool Finite(double number, double min, double max) { return !Double.IsNaN(number) && !Double.IsInfinity(number) && number >= min && number <= max; }
        private static string Name(string text, string label) { text = (text ?? "").Trim(); if (text.Length < 1 || text.Length > 40) throw new ArgumentException(label + "须为1～40字"); return text; }
    }
    public sealed class ParameterDisplayScreen
    {
        public string Id;
        public string Name;
        public List<ParameterDisplayBox> Boxes = new List<ParameterDisplayBox>();
    }
    public sealed class ParameterDisplayBox
    {
        public string Id;
        public string Name = "参数框";
        public double X = 20, Y = 20, Width = 900, Height = 480;
        public int Columns = 2;
        public double FontSize = 18, RowHeight = 54, NameWidth = 150, ValueWidth = 120;
        public string Order = "column";
        public bool ShowUnit = true, ShowStatus = true, ShowTime = true, ShowHex = true;
        public List<string> Parameters = new List<string>();
    }
    public sealed partial class Auth
    {
        public ParameterDisplayConfiguration DisplayConfiguration(Session session, List<Parameter> catalog)
        {
            lock (Gate)
            {
                var value = User(session).DisplayLayout;
                if (value == null)
                {
                    value = new ParameterDisplayConfiguration();
                    value.Screens.Add(new ParameterDisplayScreen { Id = "screen1", Name = "画面1", Boxes = new List<ParameterDisplayBox> { new ParameterDisplayBox { Id = "box1", Name = "参数监视", Parameters = ResolveSelection(session, catalog) } } });
                }
                return JsonFile.Decode<ParameterDisplayConfiguration>(JsonFile.Encode(value));
            }
        }
        public ParameterDisplayConfiguration SaveDisplayConfiguration(Session session, ParameterDisplayConfiguration incoming)
        {
            if (incoming == null) throw new ArgumentException("缺少画面配置"); incoming.Validate();
            lock (Gate)
            {
                var user = User(session); var previous = user.DisplayLayout; var oldSelection = user.Selected;
                if (incoming.Revision != (previous == null ? "initial" : previous.Revision)) throw new InvalidOperationException("显示配置已被其他页面修改，请重新载入后保存");
                var value = JsonFile.Decode<ParameterDisplayConfiguration>(JsonFile.Encode(incoming)); value.Revision = Guid.NewGuid().ToString("N");
                user.DisplayLayout = value; user.Selected = value.Screens.SelectMany(screen => screen.Boxes).SelectMany(box => box.Parameters).Distinct().ToList();
                try { Save(); } catch { user.DisplayLayout = previous; user.Selected = oldSelection; throw; }
                return JsonFile.Decode<ParameterDisplayConfiguration>(JsonFile.Encode(value));
            }
        }
    }
    public sealed partial class Server
    {
        private bool HandleParameterDisplays(HttpListenerContext ctx, Session session)
        {
            if (ctx.Request.Url.AbsolutePath != "/api/parameter-displays") return false;
            if (ctx.Request.HttpMethod == "GET") Json(ctx.Response, auth.DisplayConfiguration(session, monitor.Catalog), 200);
            else
            {
                var body = Body(ctx.Request, 8 * 1024 * 1024); object input;
                if (!body.TryGetValue("configuration", out input)) throw new ArgumentException("缺少显示配置");
                Json(ctx.Response, auth.SaveDisplayConfiguration(session, JsonFile.Decode<ParameterDisplayConfiguration>(JsonFile.Encode(input))), 200);
            }
            return true;
        }
    }
}
