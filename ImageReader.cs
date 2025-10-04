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
        public string? FileSystem { get; private set; }

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
            
            // Read filesystem metadata (v3)
            if (Version >= 3)
            {
                int fsLength = br.ReadInt32();
                if (fsLength > 0)
                {
                    var fsBytes = br.ReadBytes(fsLength);
                    FileSystem = Encoding.UTF8.GetString(fsBytes);
                }
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

    public bool VerifyAll_OLD(Action<string>? log = null, Action<long,long>? progress = null, int? parallel = null)
        {
            // Parallel verify: sequential reader + multiple decompress/hash workers
            long totalCompressed = 0;
            foreach (var e in Index) totalCompressed += e.CompressedLength;
            long doneCompressed = 0;

            int workerCount = Math.Max(1, parallel.HasValue ? parallel.Value : Environment.ProcessorCount);
            // Use reasonable worker count with adequate queue capacity
            workerCount = Math.Min(workerCount, 4); // Max 4 workers
            int bounded = workerCount * 4; // Adequate queue capacity
            var queue = new BlockingCollection<(int idx, int ulen, byte[] expectedHash, byte[] compressed)>(bounded);
            var cts = new CancellationTokenSource();
            bool failed = false;
            
            // Handle Ctrl+C gracefully
            Console.CancelKeyPress += (sender, e) => {
                Console.WriteLine("\n[INFO] Cancellation requested...");
                cts.Cancel();
                e.Cancel = true; // Prevent immediate termination
            };

            // Workers with debugging
            var workers = new List<Task>(workerCount);
            for (int w = 0; w < workerCount; w++)
            {
                int workerId = w;
                workers.Add(Task.Run(() =>
                {
                    Console.WriteLine($"[DEBUG] Worker {workerId} started");
                    using var dec = new Decompressor();
                    using var sha = SHA256.Create();
                    int processedCount = 0;
                    
                    foreach (var item in queue.GetConsumingEnumerable())
                    {
                        if (cts.IsCancellationRequested) break;
                        processedCount++;
                        
                        var chunkStartTime = DateTime.UtcNow;
                        try
                        {
                            // Check memory pressure and force GC if needed
                            var memUsage = GC.GetTotalMemory(false) / 1024 / 1024; // MB
                            if (memUsage > 2000) // 2GB threshold (more aggressive)
                            {
                                GC.Collect();
                                GC.WaitForPendingFinalizers();
                                Console.WriteLine($"[DEBUG] Worker {workerId} forced GC at {memUsage}MB");
                            }
                            
                            // Simple approach - process all chunks normally with basic memory management
                            byte[] decompressed;
                            if (item.compressed.Length > 10 * 1024 * 1024) // 10MB - basic GC for large chunks
                            {
                                Console.WriteLine($"[DEBUG] Worker {workerId} processing large chunk {item.idx}: {item.compressed.Length/1024/1024}MB (estimated {item.ulen/1024/1024}MB decompressed)");
                                
                                // Basic GC before processing large chunks
                                GC.Collect();
                                GC.WaitForPendingFinalizers();
                            }
                            
                            decompressed = dec.Unwrap(item.compressed).ToArray();
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
                            var chunkTime = DateTime.UtcNow - chunkStartTime;
                            if (chunkTime.TotalMilliseconds > 500) // Only log very slow chunks
                            {
                                var memUsage = GC.GetTotalMemory(false) / 1024 / 1024; // MB
                                Console.WriteLine($"[DEBUG] Worker {workerId} processed large chunk {item.idx}: {chunkTime.TotalMilliseconds:F0}ms, size: {item.compressed.Length/1024}KB, RAM: {memUsage}MB");
                            }
                            
                            // Force GC after processing large chunks to prevent memory buildup
                            if (item.compressed.Length > 5 * 1024 * 1024) // 5MB
                            {
                                GC.Collect();
                                GC.WaitForPendingFinalizers();
                            }
                            
                            Interlocked.Add(ref doneCompressed, item.compressed.Length);
                            progress?.Invoke(Interlocked.Read(ref doneCompressed), totalCompressed);
                            
                            if (processedCount % 50 == 0) Console.WriteLine($"[DEBUG] Worker {workerId} processed {processedCount} chunks");
                        }
                    }
                    Console.WriteLine($"[DEBUG] Worker {workerId} finished, processed {processedCount} chunks");
                }));
            }

            // Sequential reader with debugging
            try
            {
                Console.WriteLine($"[DEBUG] Starting to read {Index.Count} chunks for verification...");
                for (int i = 0; i < Index.Count && !cts.IsCancellationRequested; i++)
                {
                    if (i % 100 == 0) Console.WriteLine($"[DEBUG] Reading chunk {i}/{Index.Count} ({i * 100.0 / Index.Count:F1}%)");
                    
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
                    
                    // Process all chunks - no skipping for full verification
                    // Large chunks will be handled with memory management
                    
                    // Add with timeout to detect deadlock
                    if (!queue.TryAdd((idx, ulen, expectedHash, compressed), TimeSpan.FromSeconds(30)))
                    {
                        Console.WriteLine($"[ERROR] Failed to add chunk {idx} to queue - possible deadlock. Queue count: {queue.Count}/{bounded}");
                        failed = true;
                        cts.Cancel();
                        break;
                    }
                    
                    // Show queue status and chunk size info periodically
                    if (i % 100 == 0) 
                    {
                        var chunkSizeMB = compressed.Length / 1024 / 1024;
                        Console.WriteLine($"[DEBUG] Queue status: {queue.Count}/{bounded} chunks queued, current chunk: {chunkSizeMB}MB");
                    }
                }
                Console.WriteLine("[DEBUG] Finished reading all chunks");
            }
            finally
            {
                queue.CompleteAdding();
            }

            Task.WaitAll(workers.ToArray());
            return !failed && !cts.IsCancellationRequested;
        }

    public bool VerifyAll(Action<string>? log = null, Action<long,long>? progress = null, int? parallel = null, bool debug = false)
    {
        if (Index.Count == 0) return true;
        
        // SIMPLE SEQUENTIAL VERIFICATION - No parallel processing, no complex logic
        if (debug) Console.WriteLine($"[INFO] Starting sequential verification of {Index.Count} chunks...");
        
        // Handle Ctrl+C gracefully
        var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (sender, e) => {
            Console.WriteLine("\n[INFO] Cancellation requested...");
            cts.Cancel();
            e.Cancel = true; // Prevent immediate termination
        };
        
        long totalCompressed = 0;
        foreach (var e in Index) totalCompressed += e.CompressedLength;
        long doneCompressed = 0;
        
        using var dec = new Decompressor();
        
        for (int i = 0; i < Index.Count; i++)
        {
            if (cts.Token.IsCancellationRequested)
            {
                if (debug) Console.WriteLine($"[INFO] Verification cancelled at chunk {i}/{Index.Count}");
                return false;
            }
            
            var entry = Index[i];
            var compressed = new byte[entry.CompressedLength];
            var expectedHash = new byte[32];
            
            // Read chunk header and data
            long headerPos = entry.FileOffset - ImageFormat.ChunkHeaderSize;
            _fs.Seek(headerPos, SeekOrigin.Begin);
            
            // Read and skip header fields (idx, devOff, uLen, cLen)
            var headerBuffer = new byte[ImageFormat.ChunkHeaderSize];
            _fs.Read(headerBuffer, 0, ImageFormat.ChunkHeaderSize);
            
            // Extract hash from header (last 32 bytes)
            Array.Copy(headerBuffer, ImageFormat.ChunkHeaderSize - 32, expectedHash, 0, 32);
            
            // Read compressed data
            _fs.Seek(entry.FileOffset, SeekOrigin.Begin);
            _fs.Read(compressed, 0, compressed.Length);
            
            // Decompress and verify
            var decompressed = dec.Unwrap(compressed).ToArray();
            
            if (decompressed.Length != entry.UncompressedLength)
            {
                log?.Invoke($"Chunk {i}: length mismatch (expected {entry.UncompressedLength}, got {decompressed.Length})");
                return false;
            }
            
            var computedHash = SHA256.HashData(decompressed);
            if (!computedHash.SequenceEqual(expectedHash))
            {
                log?.Invoke($"Chunk {i}: hash mismatch");
                return false;
            }
            
            doneCompressed += entry.CompressedLength;
            progress?.Invoke(doneCompressed, totalCompressed);
            
            // Progress every 100 chunks
            if (i % 100 == 0 && debug)
            {
                Console.WriteLine($"[DEBUG] Verified {i}/{Index.Count} chunks ({(double)i/Index.Count*100:F1}%)");
            }
        }
        
        if (debug) Console.WriteLine($"[INFO] Verification completed successfully");
        return true;
    }
    }
}
