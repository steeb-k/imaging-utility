using System;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DiscUtils.Ntfs;

namespace ImagingUtility
{
    internal static class NtfsHttpServer
    {
        public static async Task ServeAsync(string host, int port, string imagePath, long volumeOffset, CancellationToken ct)
        {
            using var rai = new RandomAccessImage(imagePath);
            using var win = new ImageWindowStream(rai, volumeOffset);
#pragma warning disable CA1416
            using var ntfs = new NtfsFileSystem(win);
#pragma warning restore CA1416

            string prefix = $"http://{host}:{port}/";
            using var listener = new HttpListener();
            listener.Prefixes.Add(prefix);
            listener.Start();
            Console.WriteLine($"[serve-ntfs] Browsable HTTP at {prefix} (read-only, ACLs bypassed). Ctrl+C to stop.");
            using var reg = ct.Register(() => { try { listener.Stop(); } catch { } });

            try
            {
                while (!ct.IsCancellationRequested)
                {
                    var ctx = await listener.GetContextAsync();
                    _ = Task.Run(() => HandleRequestAsync(ctx, ntfs));
                }
            }
            catch (HttpListenerException) when (ct.IsCancellationRequested) { }
            catch (ObjectDisposedException) when (ct.IsCancellationRequested) { }
            finally
            {
                try { listener.Stop(); } catch { }
            }
        }

        private static async Task HandleRequestAsync(HttpListenerContext ctx, NtfsFileSystem ntfs)
        {
            try
            {
                string urlPath = ctx.Request.Url!.AbsolutePath; // starts with '/'
                string ntfsPath = UrlToNtfsPath(urlPath);
                if (ntfs.DirectoryExists(ntfsPath))
                {
                    await WriteDirectoryListing(ctx, ntfs, ntfsPath, urlPath);
                }
                else if (ntfs.FileExists(ntfsPath))
                {
                    await StreamFile(ctx, ntfs, ntfsPath);
                }
                else
                {
                    ctx.Response.StatusCode = 404;
                    await WriteText(ctx.Response, "Not found");
                }
            }
            catch (Exception ex)
            {
                try
                {
                    ctx.Response.StatusCode = 500;
                    await WriteText(ctx.Response, $"Error: {WebUtility.HtmlEncode(ex.Message)}");
                }
                catch { }
            }
            finally
            {
                try { ctx.Response.OutputStream.Close(); } catch { }
            }
        }

        private static string UrlToNtfsPath(string urlPath)
        {
            // map "/" -> "\\" and "/dir/file" -> "\\dir\\file"
            string decoded = Uri.UnescapeDataString(urlPath);
            string trimmed = decoded.Trim('/');
            if (string.IsNullOrEmpty(trimmed)) return "\\";
            return "\\" + trimmed.Replace('/', '\\');
        }

        private static async Task WriteDirectoryListing(HttpListenerContext ctx, NtfsFileSystem ntfs, string ntfsPath, string urlPath)
        {
            var sb = new StringBuilder();
            sb.Append("<html><head><meta charset='utf-8'><title>Index of ")
              .Append(WebUtility.HtmlEncode(urlPath))
              .Append("</title></head><body><h1>Index of ")
              .Append(WebUtility.HtmlEncode(urlPath))
              .Append("</h1><ul>");

            if (urlPath != "/")
            {
                // parent link
                var parent = urlPath.TrimEnd('/');
                var ix = parent.LastIndexOf('/');
                var up = ix > 0 ? parent.Substring(0, ix) : "/";
                sb.Append("<li><a href='")
                  .Append(WebUtility.HtmlEncode(up))
                  .Append("'>[..]</a></li>");
            }

            foreach (var dir in ntfs.GetDirectories(ntfsPath))
            {
                string name = Path.GetFileName(dir.TrimEnd('\\'));
                string href = urlPath.EndsWith("/") ? urlPath + name + "/" : urlPath + "/" + name + "/";
                sb.Append("<li>[DIR] <a href='")
                  .Append(WebUtility.HtmlEncode(href))
                  .Append("'>")
                  .Append(WebUtility.HtmlEncode(name))
                  .Append("</a></li>");
            }
            foreach (var file in ntfs.GetFiles(ntfsPath))
            {
                string name = Path.GetFileName(file);
                string href = urlPath.EndsWith("/") ? urlPath + name : urlPath + "/" + name;
                long size = 0;
                try { size = ntfs.GetFileLength(file); } catch { }
                sb.Append("<li>[FILE] <a href='")
                  .Append(WebUtility.HtmlEncode(href))
                  .Append("'>")
                  .Append(WebUtility.HtmlEncode(name))
                  .Append("</a> (")
                  .Append(size)
                  .Append(" bytes)</li>");
            }
            sb.Append("</ul><hr><small>ImagingUtility NTFS browser (read-only; ACLs bypassed)</small></body></html>");
            var bytes = Encoding.UTF8.GetBytes(sb.ToString());
            ctx.Response.ContentType = "text/html; charset=utf-8";
            ctx.Response.ContentLength64 = bytes.Length;
            await ctx.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length);
        }

        private static async Task StreamFile(HttpListenerContext ctx, NtfsFileSystem ntfs, string ntfsPath)
        {
            ctx.Response.ContentType = "application/octet-stream";
            ctx.Response.AddHeader("Content-Disposition", $"attachment; filename=\"{Path.GetFileName(ntfsPath)}\"");
            using var s = ntfs.OpenFile(ntfsPath, FileMode.Open, FileAccess.Read);
            ctx.Response.ContentLength64 = s.Length;
            await s.CopyToAsync(ctx.Response.OutputStream);
        }

        private static Task WriteText(HttpListenerResponse resp, string text)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(text);
            resp.ContentType = "text/plain; charset=utf-8";
            resp.ContentLength64 = bytes.Length;
            return resp.OutputStream.WriteAsync(bytes, 0, bytes.Length);
        }
    }
}
