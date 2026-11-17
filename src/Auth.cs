using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

namespace TelemetryMonitor
{
    public sealed partial class Auth
    {
        public readonly object Gate = new object();
        private readonly string file;
        private List<Account> users;
        private readonly Dictionary<string, Session> sessions = new Dictionary<string, Session>();
        private readonly Dictionary<string, List<DateTime>> failures = new Dictionary<string, List<DateTime>>();
        public Auth(string root)
        {
            file = Path.Combine(root, "data", "accounts.json");
            users = File.Exists(file) ? JsonFile.Load<List<Account>>(file) : new List<Account>();
        }
        public bool SetupRequired { get { lock (Gate) return users.Count == 0; } }
        public static string RandomToken()
        {
            byte[] bytes = new byte[32]; using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
            return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }
        private static string Hash(string password, string salt)
        {
            using (var kdf = new Rfc2898DeriveBytes(password, Convert.FromBase64String(salt), 120000)) return Convert.ToBase64String(kdf.GetBytes(32));
        }
        private static bool Equal(string a, string b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            int diff = 0; for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i]; return diff == 0;
        }
        private static void SetPassword(Account user, string password)
        {
            if (password == null || password.Length < 8 || password.Length > 128) throw new ArgumentException("密码长度须为8～128个字符");
            byte[] salt = new byte[24]; using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(salt);
            user.Salt = Convert.ToBase64String(salt); user.PasswordHash = Hash(password, user.Salt);
        }
        public Account Add(string username, string name, string password, bool first, bool admin, List<string> selected)
        {
            lock (Gate)
            {
                if (first && users.Count != 0) throw new InvalidOperationException("管理员已初始化");
                if (users.Count >= 64) throw new ArgumentException("账户数量已达上限");
                username = (username ?? "").Trim(); name = (name ?? "").Trim();
                if (!System.Text.RegularExpressions.Regex.IsMatch(username, "^[A-Za-z0-9_.-]{3,32}$")) throw new ArgumentException("账号使用3～32位字母、数字、下划线、点或短横线");
                if (name.Length == 0 || name.Length > 40) throw new ArgumentException("请输入1～40字的操作员姓名");
                if (users.Any(u => String.Equals(u.Username, username, StringComparison.OrdinalIgnoreCase))) throw new ArgumentException("账号已存在");
                var user = new Account { Username = username, DisplayName = name, Admin = first || admin, Selected = selected.Take(12).ToList() };
                SetPassword(user, password); users.Add(user); Save(); return user;
            }
        }
        private void Save() { JsonFile.Save(file, users); }
        public Session Login(string username, string password)
        {
            lock (Gate)
            {
                DateTime now = DateTime.UtcNow;
                username = (username ?? "").Trim();
                string key = username.ToLowerInvariant();
                if (key.Length > 32 || password == null || password.Length > 128) throw new ArgumentException("账号或密码错误");
                List<DateTime> attempts;
                if (!failures.TryGetValue(key, out attempts))
                {
                    if (failures.Count > 1000) failures.Clear();
                    failures[key] = attempts = new List<DateTime>();
                }
                attempts.RemoveAll(t => (now - t).TotalSeconds > 60);
                if (attempts.Count >= 5) throw new ArgumentException("登录失败次数过多，请1分钟后重试");
                Account user = users.FirstOrDefault(u => String.Equals(u.Username, username, StringComparison.OrdinalIgnoreCase));
                string candidate = Hash(password, user == null ? Convert.ToBase64String(new byte[24]) : user.Salt);
                if (user == null || !Equal(candidate, user.PasswordHash)) { attempts.Add(now); throw new ArgumentException("账号或密码错误"); }
                attempts.Clear();
                foreach (string expired in sessions.Where(p => p.Value.Expires < now).Select(p => p.Key).ToList()) sessions.Remove(expired);
                if (sessions.Count >= 128 || sessions.Values.Count(s => s.Username == user.Username) >= 8) throw new InvalidOperationException("登录会话过多，请退出其他窗口后重试");
                var session = new Session { Token = RandomToken(), Csrf = RandomToken(), Username = user.Username, Expires = now.AddHours(12) };
                sessions[session.Token] = session; return session;
            }
        }
        public Session Find(string token)
        {
            lock (Gate)
            {
                Session session;
                if (token == null || !sessions.TryGetValue(token, out session) || session.Expires < DateTime.UtcNow) return null;
                return session;
            }
        }
        public Account User(Session s) { lock (Gate) return users.First(u => u.Username == s.Username); }
        public void Logout(Session s) { lock (Gate) sessions.Remove(s.Token); }
        public bool CheckCsrf(Session session, string token) { return Equal(session.Csrf, token); }
        public List<string> Selection(Session s) { lock (Gate) return User(s).Selected.ToList(); }
        public List<string> ResolveSelection(Session s, List<Parameter> catalog)
        {
            lock (Gate)
            {
                return CachedSelection(User(s),catalog);
            }
        }
        public void Select(Session s, List<string> ids)
        {
            lock (Gate) { User(s).Selected = ids.Distinct().ToList(); Save(); }
        }
        public object PublicUser(Session s)
        {
            lock (Gate) { Account u = User(s); return new { username = u.Username, name = u.DisplayName, admin = u.Admin, selected = u.Selected.ToArray(), csrf = s.Csrf }; }
        }
        public object ListUsers()
        {
            lock (Gate) return users.Select(u => new { username = u.Username, name = u.DisplayName, admin = u.Admin }).ToArray();
        }
        public void ChangePassword(Session s, string oldPassword, string newPassword)
        {
            lock (Gate)
            {
                Account u = User(s);
                if (oldPassword == null || oldPassword.Length > 128 || !Equal(Hash(oldPassword, u.Salt), u.PasswordHash)) throw new ArgumentException("原密码错误");
                SetPassword(u, newPassword); Save(); Revoke(u.Username);
            }
        }
        public void ResetPassword(string username, string password)
        {
            lock (Gate)
            {
                Account u = users.FirstOrDefault(x => x.Username == username);
                if (u == null) throw new ArgumentException("账号不存在");
                SetPassword(u, password); Save(); Revoke(username);
            }
        }
        private void Revoke(string username)
        {
            foreach (string key in sessions.Where(p => p.Value.Username == username).Select(p => p.Key).ToList()) sessions.Remove(key);
        }
        public bool OpenStream(Session s)
        {
            lock (Gate) { if (s.Streams >= 3 || sessions.Values.Sum(x => x.Streams) >= 24) return false; s.Streams++; return true; }
        }
        public void CloseStream(Session s) { lock (Gate) s.Streams--; }
    }
}
