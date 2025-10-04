using System;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ExFat.DiscUtils;

namespace ImagingUtility
{
    internal static class ExFatWebDavServer
    {
        public static async Task ServeAsync(string host, int port, string imagePath, long volumeOffset, CancellationToken ct)
        {
            using var rai = new RandomAccessImage(imagePath);
            using var win = new ImageWindowStream(rai, volumeOffset);
#pragma warning disable CA1416
            using var exfat = new ExFatFileSystem(win);
#pragma warning restore CA1416

            string prefix = $"http://{host}:{port}/";
            using var listener = new HttpListener();
            listener.Prefixes.Add(prefix);
            listener.Start();
            Console.WriteLine($"[exfat-webdav] WebDAV at {prefix} (read-only). Use 'net use Z: {prefix}' to map. Ctrl+C to stop.");
            using var reg = ct.Register(() => { try { listener.Stop(); } catch { } });

            try
            {
                while (!ct.IsCancellationRequested)
                {
                    var ctx = await listener.GetContextAsync();
                    _ = Task.Run(() => HandleRequestAsync(ctx, exfat));
                }
            }
            catch (HttpListenerException) when (ct.IsCancellationRequested) { }
            catch (ObjectDisposedException) when (ct.IsCancellationRequested) { }
            finally { try { listener.Stop(); } catch { } }
        }

        private static async Task HandleRequestAsync(HttpListenerContext ctx, ExFatFileSystem exfat)
        {
            try
            {
                ctx.Response.Headers["DAV"] = "1,2";
                ctx.Response.Headers["MS-Author-Via"] = "DAV";
                string method = ctx.Request.HttpMethod.ToUpperInvariant();
                string path = ctx.Request.Url!.AbsolutePath;
                string exfatPath = UrlToExFatPath(path);

                switch (method)
                {
                    case "OPTIONS":
                        ctx.Response.AddHeader("Allow", "OPTIONS, PROPFIND, GET, HEAD");
                        ctx.Response.StatusCode = 200;
                        break;
                    case "PROPFIND":
                        await HandlePropFind(ctx, exfat, exfatPath);
                        break;
                    case "GET":
                        await HandleGetOrHead(ctx, exfat, exfatPath, head: false);
                        break;
                    case "HEAD":
                        await HandleGetOrHead(ctx, exfat, exfatPath, head: true);
                        break;
                    default:
                        ctx.Response.StatusCode = 405; // Method Not Allowed
                        break;
                }
            }
            catch (Exception ex)
            {
                try
                {
                    ctx.Response.StatusCode = 500;
                    await WriteText(ctx.Response, ex.Message);
                }
                catch { }
            }
            finally
            {
                try { ctx.Response.OutputStream.Close(); } catch { }
            }
        }

        private static string UrlToExFatPath(string urlPath)
        {
            string decoded = Uri.UnescapeDataString(urlPath);
            string trimmed = decoded.Trim('/');
            if (string.IsNullOrEmpty(trimmed)) return "\\";
            return "\\" + trimmed.Replace('/', '\\');
        }

        private static async Task HandlePropFind(HttpListenerContext ctx, ExFatFileSystem exfat, string exfatPath)
        {
            // Depth: 0 or 1 supported
            string depth = ctx.Request.Headers["Depth"] ?? "0";
            bool recurse = depth == "1";
            var sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?>")
              .Append("<D:multistatus xmlns:D=\"DAV:\">\n");

            void EmitEntry(string webPath, bool isDir, long size, DateTime? mod)
            {
                string display = webPath.TrimEnd('/');
                int ix = display.LastIndexOf('/');
                string name = ix >= 0 ? display.Substring(ix + 1) : display;
                if (string.IsNullOrEmpty(name)) name = "/";
                sb.Append("  <D:response>\n")
                  .Append("    <D:href>")
                  .Append(WebUtility.HtmlEncode(webPath))
                  .Append("</D:href>\n")
                  .Append("    <D:propstat>\n")
                  .Append("      <D:prop>\n")
                  .Append("        <D:displayname>")
                  .Append(WebUtility.HtmlEncode(name))
                  .Append("</D:displayname>\n")
                  .Append("        <D:resourcetype>");
                if (isDir) sb.Append("<D:collection/>");
                sb.Append("</D:resourcetype>\n");
                if (!isDir)
                {
                    sb.Append("        <D:getcontentlength>")
                      .Append(size)
                      .Append("</D:getcontentlength>\n")
                      .Append("        <D:getcontenttype>application/octet-stream</D:getcontenttype>\n");
                }
                if (mod.HasValue)
                {
                    sb.Append("        <D:getlastmodified>")
                      .Append(mod.Value.ToUniversalTime().ToString("r"))
                      .Append("</D:getlastmodified>\n");
                }
                sb.Append("      </D:prop>\n")
                  .Append("      <D:status>HTTP/1.1 200 OK</D:status>\n")
                  .Append("    </D:propstat>\n")
                  .Append("  </D:response>\n");
            }

            // Normalize web path with trailing slash for directories
            string webPath = ctx.Request.Url!.AbsolutePath;

            if (exfat.DirectoryExists(exfatPath))
            {
                if (!webPath.EndsWith("/")) webPath += "/";
                EmitEntry(webPath, isDir: true, size: 0, mod: null);
                if (recurse)
                {
                    foreach (var dir in exfat.GetDirectories(exfatPath))
                    {
                        string name = Path.GetFileName(dir.TrimEnd('\\'));
                        EmitEntry(webPath + name + "/", true, 0, null);
                    }
                    foreach (var file in exfat.GetFiles(exfatPath))
                    {
                        string name = Path.GetFileName(file);
                        long size = 0; try { size = exfat.GetFileLength(file); } catch { }
                        EmitEntry(webPath + name, false, size, null);
                    }
                }
            }
            else if (exfat.FileExists(exfatPath))
            {
                long size = 0; try { size = exfat.GetFileLength(exfatPath); } catch { }
                EmitEntry(webPath, false, size, null);
            }
            else
            {
                ctx.Response.StatusCode = 404; await WriteText(ctx.Response, "Not found"); return;
            }

            byte[] bytes = Encoding.UTF8.GetBytes(sb.Append("</D:multistatus>").ToString());
            ctx.Response.StatusCode = 207; // Multi-Status
            ctx.Response.ContentType = "application/xml; charset=utf-8";
            ctx.Response.ContentLength64 = bytes.Length;
            await ctx.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length);
        }

        private static async Task HandleGetOrHead(HttpListenerContext ctx, ExFatFileSystem exfat, string exfatPath, bool head)
        {
            if (!exfat.FileExists(exfatPath)) { ctx.Response.StatusCode = 404; return; }
            using var s = exfat.OpenFile(exfatPath, FileMode.Open, FileAccess.Read);
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "application/octet-stream";
            ctx.Response.ContentLength64 = s.Length;
            if (!head)
            {
                await s.CopyToAsync(ctx.Response.OutputStream);
            }
        }

        private static Task WriteText(HttpListenerResponse resp, string text)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            resp.ContentType = "text/plain; charset=utf-8";
            resp.ContentLength64 = bytes.Length;
            return resp.OutputStream.WriteAsync(bytes, 0, bytes.Length);
        }
    }
}



