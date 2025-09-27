using System;
using System.Buffers.Binary;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ImagingUtility
{
    // Minimal, read-only, DevIO-like proxy that serves block reads from a compressed image.
    // Protocol (TCP or named pipe style payload):
    // - On connect: server writes 8-byte little-endian total device length.
    // - Requests: 1 byte op. 'R' -> read, 'Q' -> quit.
    //   For 'R': client sends 8-byte little-endian offset and 4-byte little-endian length.
    //   Server responds with <length> bytes (clamped to EOF; zero-filled past end).
    // Notes: This is intentionally simple and read-only; intended for use with tools that support DevIO/Proxy-like sources.
    internal static class DevioProxyServer
    {
        public static async Task ServeTcpAsync(string host, int port, string imagePath, int cacheChunks = 4, long? deviceOffset = null, long? deviceLength = null, CancellationToken ct = default)
        {
            using var rai = new RandomAccessImage(imagePath, cacheChunks);
            long start = Math.Max(0, deviceOffset ?? 0);
            long length = Math.Max(0, Math.Min(deviceLength ?? rai.Length - start, rai.Length - start));
            var listener = new TcpListener(IPAddress.Parse(host), port);
            listener.Start();
            Console.WriteLine($"[serve-proxy] Listening on tcp://{host}:{port}  (size={length} bytes, window={start}+{length}). Press Ctrl+C to stop.");
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    var client = await listener.AcceptTcpClientAsync(ct);
                    _ = HandleTcpClientAsync(client, rai, start, length, ct);
                }
            }
            finally
            {
                listener.Stop();
            }
        }

        public static async Task ServePipeAsync(string pipeName, string imagePath, int cacheChunks = 4, long? deviceOffset = null, long? deviceLength = null, CancellationToken ct = default)
        {
            using var rai = new RandomAccessImage(imagePath, cacheChunks);
            long start = Math.Max(0, deviceOffset ?? 0);
            long length = Math.Max(0, Math.Min(deviceLength ?? rai.Length - start, rai.Length - start));
            Console.WriteLine($"[serve-proxy] Listening on pipe://{pipeName}  (size={length} bytes, window={start}+{length}). Press Ctrl+C to stop.");
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                    await server.WaitForConnectionAsync(ct);
                    await HandleStreamAsync(server, rai, start, length, ct);
                }
                catch (OperationCanceledException) { break; }
                catch { await Task.Delay(200, ct); }
            }
        }

        private static async Task HandleTcpClientAsync(TcpClient client, RandomAccessImage rai, long windowOffset, long windowLength, CancellationToken ct)
        {
            using (client)
            using (var ns = client.GetStream())
            {
                await HandleStreamAsync(ns, rai, windowOffset, windowLength, ct);
            }
        }

        private static async Task<int> ReadExactAsync(Stream s, byte[] buf, int off, int len, CancellationToken ct)
        {
            int readTotal = 0;
            while (readTotal < len)
            {
                int r = await s.ReadAsync(buf, off + readTotal, len - readTotal, ct);
                if (r == 0) return readTotal;
                readTotal += r;
            }
            return readTotal;
        }

        private static async Task HandleStreamAsync(Stream ns, RandomAccessImage rai, long windowOffset, long windowLength, CancellationToken ct)
        {
            // Send size
            var sizeBytes = new byte[8];
            BinaryPrimitives.WriteInt64LittleEndian(sizeBytes, windowLength);
            await ns.WriteAsync(sizeBytes, 0, sizeBytes.Length, ct);

            var header = new byte[1];
            var offLen = new byte[12]; // 8 + 4
            var buffer = new byte[1024 * 1024];
            while (!ct.IsCancellationRequested)
            {
                int r = await ReadExactAsync(ns, header, 0, 1, ct);
                if (r == 0) break; // closed
                char op = (char)header[0];
                if (op == 'Q') break;
                else if (op == 'R')
                {
                    r = await ReadExactAsync(ns, offLen, 0, 12, ct);
                    if (r == 0) break;
                    long offset = BinaryPrimitives.ReadInt64LittleEndian(offLen.AsSpan(0, 8));
                    int length = BinaryPrimitives.ReadInt32LittleEndian(offLen.AsSpan(8, 4));
                    // Clamp to window
                    if (offset < 0) offset = 0;
                    long maxLen = Math.Max(0, windowLength - offset);
                    if (length > maxLen) length = (int)maxLen;
                    int remaining = length;
                    long pos = windowOffset + offset;
                    while (remaining > 0)
                    {
                        int toRead = Math.Min(buffer.Length, remaining);
                        await rai.ReadAsync(pos, buffer, 0, toRead, ct);
                        await ns.WriteAsync(buffer, 0, toRead, ct);
                        pos += toRead; remaining -= toRead;
                    }
                }
                else
                {
                    // Unknown op -> close
                    break;
                }
            }
        }
    }
}
