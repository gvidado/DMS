using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace TelemetryMonitor
{
    public sealed partial class Server
    {
        private sealed class WebContent
        {
            internal byte[] Plain, Compressed;
            internal string Tag;
            internal DateTime Modified;
        }

        private readonly object webAssetGate = new object();
        private readonly object webCatalogGate = new object();
        private readonly Dictionary<string, WebContent> webAssets = new Dictionary<string, WebContent>(StringComparer.Ordinal);
        private List<Parameter> webCatalog;
        private WebContent webCatalogContent;

        private static WebContent PrepareContent(byte[] bytes)
        {
            var content = new WebContent { Plain = bytes };
            if (bytes.Length >= 1024)
            {
                using (var buffer = new MemoryStream())
                {
                    using (var zip = new GZipStream(buffer, CompressionLevel.Fastest, true))
                        zip.Write(bytes, 0, bytes.Length);
                    if (buffer.Length < bytes.Length) content.Compressed = buffer.ToArray();
                }
            }
            return content;
        }

        private static bool AcceptsGzip(string value)
        {
            if (String.IsNullOrEmpty(value)) return false;
            double wildcard = 0;
            foreach (var token in value.Split(','))
            {
                var parts = token.Split(';');
                string coding = parts[0].Trim();
                bool gzip = String.Equals(coding, "gzip", StringComparison.OrdinalIgnoreCase);
                if (!gzip && coding != "*") continue;
                double quality = 1;
                for (int i = 1; i < parts.Length; i++)
                {
                    string item = parts[i].Trim();
                    if (!item.StartsWith("q=", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!Double.TryParse(item.Substring(2), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out quality)
                        || quality < 0 || quality > 1) quality = 0;
                }
                if (gzip) return quality > 0;
                wildcard = quality;
            }
            return wildcard > 0;
        }

        private static void SendWebContent(HttpListenerContext ctx, WebContent content, string contentType)
        {
            var response = ctx.Response;
            bool gzip = content.Compressed != null && AcceptsGzip(ctx.Request.Headers["Accept-Encoding"]);
            if (content.Compressed != null) response.Headers["Vary"] = "Accept-Encoding";
            if (content.Tag != null)
            {
                string tag = "\"" + content.Tag + (gzip ? "-gzip" : "") + "\"";
                response.Headers["Cache-Control"] = "no-cache";
                response.Headers["ETag"] = tag;
                string condition = ctx.Request.Headers["If-None-Match"];
                if (condition != null)
                {
                    foreach (string candidate in condition.Split(','))
                    {
                        string trimmed = candidate.Trim();
                        if (trimmed == "*" || trimmed == tag || trimmed == "W/" + tag)
                        {
                            response.StatusCode = 304;
                            return;
                        }
                    }
                }
            }
            if (gzip) response.Headers["Content-Encoding"] = "gzip";
            byte[] data = gzip ? content.Compressed : content.Plain;
            response.ContentType = contentType;
            response.ContentLength64 = data.Length;
            response.OutputStream.Write(data, 0, data.Length);
        }

        private void ServeWebAsset(HttpListenerContext ctx, string name)
        {
            WebContent content;
            lock (webAssetGate)
            {
                var file = new FileInfo(Path.Combine(root, "web", name));
                if (!webAssets.TryGetValue(name, out content) || content.Modified != file.LastWriteTimeUtc
                    || content.Plain.LongLength != file.Length)
                {
                    DateTime modified = file.LastWriteTimeUtc;
                    content = PrepareContent(File.ReadAllBytes(file.FullName));
                    using (var sha = SHA256.Create()) content.Tag = Convert.ToBase64String(sha.ComputeHash(content.Plain));
                    content.Modified = modified;
                    webAssets[name] = content;
                }
            }
            string contentType = name.EndsWith(".js") ? "application/javascript" : name.EndsWith(".css") ? "text/css" :
                name.EndsWith(".svg") ? "image/svg+xml" : "text/html";
            SendWebContent(ctx, content, contentType + "; charset=utf-8");
        }

        private void ServeCatalog(HttpListenerContext ctx)
        {
            WebContent content;
            lock (webCatalogGate)
            {
                // Each committed decoder change publishes a new catalog list.
                var catalog = liveMonitor.Catalog;
                if (!ReferenceEquals(catalog, webCatalog))
                {
                    webCatalogContent = PrepareContent(Encoding.UTF8.GetBytes(JsonFile.Encode(catalog)));
                    webCatalog = catalog;
                }
                content = webCatalogContent;
            }
            SendWebContent(ctx, content, "application/json; charset=utf-8");
        }
    }
}
