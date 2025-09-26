using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using ZstdSharp;

namespace ImagingUtility
{
    internal class ImageReader : IDisposable
    {
        private FileStream _fs;
        public int Version { get; private set; }
        public int SectorSize { get; private set; }
        public int ChunkSize { get; private set; }
        public long IndexStart { get; private set; }
        public List<IndexEntry> Index { get; } = new();
    public long DeviceLength { get; private set; }

        public ImageReader(string path)
        {
            _fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            ReadHeader(_fs);
            ReadIndex(_fs);
        }

        public void Dispose()
        {
            _fs?.Dispose();
            _fs = null!;
        }

        public void ReadHeader(Stream s)
        {
            s.Seek(0, SeekOrigin.Begin);
            using var br = new BinaryReader(s, Encoding.UTF8, leaveOpen: true);
            var magic = br.ReadBytes(4);
            if (!magic.AsSpan().SequenceEqual(ImageFormat.HeaderMagic))
                throw new InvalidDataException("Not an IMG1 file");
            Version = br.ReadInt32();
            SectorSize = br.ReadInt32();
            ChunkSize = br.ReadInt32();
            if (Version >= 2)
            {
                DeviceLength = br.ReadInt64();
            }
        }

        public void ReadIndex(Stream s)
        {
            if (!s.CanSeek) throw new InvalidOperationException("Stream must be seekable");
            s.Seek(-12, SeekOrigin.End);
            using (var br = new BinaryReader(s, Encoding.UTF8, leaveOpen: true))
            {
                var tailMagic = br.ReadBytes(4);
                if (!tailMagic.AsSpan().SequenceEqual(ImageFormat.TailMagic))
                    throw new InvalidDataException("Missing TAIL footer");
                IndexStart = br.ReadInt64();
            }
            s.Seek(IndexStart, SeekOrigin.Begin);
            using (var br = new BinaryReader(s, Encoding.UTF8, leaveOpen: true))
            {
                var idxMagic = br.ReadBytes(4);
                if (!idxMagic.AsSpan().SequenceEqual(ImageFormat.IndexMagic))
                    throw new InvalidDataException("Missing IDX1");
                int count = br.ReadInt32();
                Index.Clear();
                for (int i = 0; i < count; i++)
                {
                    var e = new IndexEntry
                    {
                        DeviceOffset = br.ReadInt64(),
                        FileOffset = br.ReadInt64(),
                        UncompressedLength = br.ReadInt32(),
                        CompressedLength = br.ReadInt32()
                    };
                    Index.Add(e);
                }
                if (Version < 2)
                {
                    if (Index.Count > 0)
                    {
                        var last = Index[^1];
                        DeviceLength = last.DeviceOffset + last.UncompressedLength;
                    }
                    else
                    {
                        DeviceLength = 0;
                    }
                }
            }
        }

        public (long nextDeviceOffset, int nextChunkIndex) ComputeResumePoint()
        {
            if (Index.Count == 0) return (0, 0);
            var last = Index[^1];
            return (last.DeviceOffset + last.UncompressedLength, Index.Count);
        }

    public bool VerifyAll(Action<string>? log = null, Action<long,long>? progress = null, int? parallel = null)
        {
            // Parallel verify: sequential reader + multiple decompress/hash workers
            long totalCompressed = 0;
            foreach (var e in Index) totalCompressed += e.CompressedLength;
            long doneCompressed = 0;

            int workerCount = Math.Max(1, parallel.HasValue ? parallel.Value : Environment.ProcessorCount);
            int bounded = workerCount * 2;
            var queue = new BlockingCollection<(int idx, int ulen, byte[] expectedHash, byte[] compressed)>(bounded);
            var cts = new CancellationTokenSource();
            bool failed = false;

            // Workers
            var workers = new List<Task>(workerCount);
            for (int w = 0; w < workerCount; w++)
            {
                workers.Add(Task.Run(() =>
                {
                    using var dec = new Decompressor();
                    using var sha = SHA256.Create();
                    foreach (var item in queue.GetConsumingEnumerable())
                    {
                        if (cts.IsCancellationRequested) break;
                        try
                        {
                            var decompressed = dec.Unwrap(item.compressed).ToArray();
                            if (decompressed.Length != item.ulen)
                            {
                                failed = true;
                                log?.Invoke($"Chunk {item.idx}: length mismatch (expected {item.ulen}, got {decompressed.Length})");
                                cts.Cancel();
                                break;
                            }
                            var actual = sha.ComputeHash(decompressed);
                            if (!actual.AsSpan().SequenceEqual(item.expectedHash))
                            {
                                failed = true;
                                log?.Invoke($"Chunk {item.idx}: checksum mismatch");
                                cts.Cancel();
                                break;
                            }
                        }
                        catch (Exception ex)
                        {
                            failed = true;
                            log?.Invoke($"Chunk {item.idx}: decompress error: {ex.Message}");
                            cts.Cancel();
                            break;
                        }
                        finally
                        {
                            Interlocked.Add(ref doneCompressed, item.compressed.Length);
                            progress?.Invoke(Interlocked.Read(ref doneCompressed), totalCompressed);
                        }
                    }
                }));
            }

            // Sequential reader
            try
            {
                for (int i = 0; i < Index.Count && !cts.IsCancellationRequested; i++)
                {
                    var e = Index[i];
                    long headerPos = e.FileOffset - ImageFormat.ChunkHeaderSize;
                    _fs.Seek(headerPos, SeekOrigin.Begin);
                    using var br = new BinaryReader(_fs, Encoding.UTF8, leaveOpen: true);
                    int idx = br.ReadInt32();
                    long devOff = br.ReadInt64();
                    int ulen = br.ReadInt32();
                    int clen = br.ReadInt32();
                    var expectedHash = br.ReadBytes(32);
                    var compressed = br.ReadBytes(clen);
                    if (compressed.Length != clen)
                    {
                        failed = true;
                        log?.Invoke($"Chunk {idx}: unexpected EOF");
                        cts.Cancel();
                        break;
                    }
                    queue.Add((idx, ulen, expectedHash, compressed));
                }
            }
            finally
            {
                queue.CompleteAdding();
            }

            Task.WaitAll(workers.ToArray());
            return !failed && !cts.IsCancellationRequested;
        }
    }
}
