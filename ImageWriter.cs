using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using ZstdSharp;
using System.Threading;

namespace ImagingUtility
{
    // Adjustable pool of compressor workers that can scale up/down at runtime.
    internal class AdjustableCompressorPool
    {
        private readonly System.Collections.Concurrent.BlockingCollection<(int idx, long devOff, byte[] data)> _in;
        private readonly System.Collections.Concurrent.BlockingCollection<(int idx, long devOff, int ulen, byte[] hash, byte[] comp)> _out;
        private readonly List<Task> _workers = new List<Task>();
        private readonly object _lock = new object();
        private int _retireRequests = 0;
        private int _activeWorkers = 0; // current number of worker tasks running

        public AdjustableCompressorPool(System.Collections.Concurrent.BlockingCollection<(int idx, long devOff, byte[] data)> input,
                                        System.Collections.Concurrent.BlockingCollection<(int idx, long devOff, int ulen, byte[] hash, byte[] comp)> output,
                                        int initialDegree)
        {
            _in = input;
            _out = output;
            SetDegree(initialDegree);
        }

        public IReadOnlyList<Task> SnapshotTasks()
        {
            lock (_lock) { return _workers.ToArray(); }
        }

        public void SetDegree(int desired)
        {
            if (desired < 1) desired = 1;
            lock (_lock)
            {
                int delta = desired - _activeWorkers;
                if (delta > 0)
                {
                    for (int i = 0; i < delta; i++)
                    {
                        var t = Task.Run(WorkerLoop);
                        _workers.Add(t);
                        _activeWorkers++;
                    }
                }
                else if (delta < 0)
                {
                    // Request some workers to retire after their current item
                    System.Threading.Interlocked.Add(ref _retireRequests, -delta);
                    // _activeWorkers will be decremented by workers upon exit
                }
            }
        }

        private bool TryConsumeRetireToken()
        {
            while (true)
            {
                int cur = System.Threading.Volatile.Read(ref _retireRequests);
                if (cur <= 0) return false;
                if (System.Threading.Interlocked.CompareExchange(ref _retireRequests, cur - 1, cur) == cur)
                    return true;
            }
        }

        private void WorkerLoop()
        {
            using var compressor = new Compressor(3);
            using var sha = SHA256.Create();
            try
            {
                foreach (var item in _in.GetConsumingEnumerable())
                {
                    var hash = sha.ComputeHash(item.data);
                    var comp = compressor.Wrap(item.data).ToArray();
                    _out.Add((item.idx, item.devOff, item.data.Length, hash, comp));

                    // After processing an item, decide if this worker should retire
                    if (TryConsumeRetireToken())
                        break;
                }
            }
            finally
            {
                System.Threading.Interlocked.Decrement(ref _activeWorkers);
            }
        }
    }

    internal static class ImageFormat
    {
        public static readonly byte[] HeaderMagic = Encoding.ASCII.GetBytes("IMG1");
        public static readonly byte[] IndexMagic = Encoding.ASCII.GetBytes("IDX1");
        public static readonly byte[] TailMagic = Encoding.ASCII.GetBytes("TAIL");
        public const int Version = 3; // v3 adds filesystem metadata to header
        public const int ChunkHeaderSize = 4 + 8 + 4 + 4 + 32; // idx + devOff + uLen + cLen + sha256
    }

    internal struct IndexEntry
    {
        public long DeviceOffset;
        public long FileOffset; // payload start
        public int UncompressedLength;
        public int CompressedLength;
    }

    internal class ChunkedZstdWriter
    {
        private readonly Stream _out;
        private readonly int _sectorSize;
        private readonly int _chunkSize;
        private readonly List<IndexEntry> _index;
        private bool _wroteHeader;
        private long _deviceLength;
        private readonly string? _filesystem;

    public ChunkedZstdWriter(Stream outStream, int sectorSize, int chunkSize = 64 * 1024 * 1024, bool append = false, List<IndexEntry>? existingIndex = null, long deviceLength = 0, string? filesystem = null)
        {
            _out = outStream ?? throw new ArgumentNullException(nameof(outStream));
            _sectorSize = sectorSize;
            _chunkSize = AlignChunkSize(chunkSize, sectorSize);
            _index = existingIndex ?? new List<IndexEntry>();
            _wroteHeader = append;
            _deviceLength = deviceLength;
            _filesystem = filesystem;
            if (!append)
            {
                WriteHeader();
            }
        }

        private static int AlignChunkSize(int size, int sector)
        {
            if (size % sector == 0) return size;
            return (size / sector) * sector;
        }

        private void WriteHeader()
        {
            using var bw = new BinaryWriter(_out, Encoding.UTF8, leaveOpen: true);
            bw.Write(ImageFormat.HeaderMagic);
            bw.Write(ImageFormat.Version);
            bw.Write(_sectorSize);
            bw.Write(_chunkSize);
            bw.Write(_deviceLength);
            
            // Write filesystem metadata (v3)
            if (!string.IsNullOrEmpty(_filesystem))
            {
                var fsBytes = Encoding.UTF8.GetBytes(_filesystem);
                bw.Write(fsBytes.Length); // Length of filesystem string
                bw.Write(fsBytes); // Filesystem string
            }
            else
            {
                bw.Write(0); // No filesystem metadata
            }
            
            bw.Flush();
            _wroteHeader = true;
        }

        public async Task<(int chunksWritten, long lastDeviceOffset)> WriteFromAsync(IBlockReader reader, long startDeviceOffset = 0, long? maxBytes = null, Action<long,long>? progress = null, int? parallel = null, Func<int>? getDesiredParallel = null, int pipelineDepth = 2, bool enableAdaptiveConcurrency = false)
        {
            if (!_wroteHeader) WriteHeader();
            
            var pipelineStopwatch = System.Diagnostics.Stopwatch.StartNew();
            Console.WriteLine($"Starting pipeline with {parallel ?? Environment.ProcessorCount} workers, depth {pipelineDepth}");

            // Parallel pipeline: read -> compress -> write (ordered)
            using var _ = new System.Threading.CancellationTokenSource(); // placeholder to preserve prior using
            int workerCount = Math.Max(1, parallel.HasValue ? parallel.Value : Environment.ProcessorCount);
            int bounded = Math.Max(2, workerCount * Math.Max(1, pipelineDepth));
            var readBlocks = new System.Collections.Concurrent.BlockingCollection<(int idx, long devOff, byte[] data)>(bounded);
            var compBlocks = new System.Collections.Concurrent.BlockingCollection<(int idx, long devOff, int ulen, byte[] hash, byte[] comp)>(bounded);

            long remaining = reader.TotalSize - startDeviceOffset;
            if (maxBytes.HasValue && maxBytes.Value >= 0) remaining = Math.Min(remaining, maxBytes.Value);
            long deviceOffset = startDeviceOffset;
            if (startDeviceOffset > 0) reader.Seek(startDeviceOffset);
            int nextIndex = _index.Count;
            int nextWriteIndex = _index.Count;

            // Adjustable compressor worker pool
            var pool = new AdjustableCompressorPool(readBlocks, compBlocks, workerCount);
            CancellationTokenSource? monitorCts = null;
            Task? monitorTask = null;
            if (getDesiredParallel != null)
            {
                monitorCts = new CancellationTokenSource();
                monitorTask = Task.Run(async () =>
                {
                    int last = workerCount;
                    while (!monitorCts.IsCancellationRequested)
                    {
                        try
                        {
                            int desired = getDesiredParallel();
                            desired = Math.Max(1, Math.Min(desired, Environment.ProcessorCount * 2));
                            if (desired != last)
                            {
                                pool.SetDegree(desired);
                                last = desired;
                            }
                        }
                        catch { }
                        try { await Task.Delay(1000, monitorCts.Token); } catch { }
                    }
                });
            }

            // Writer (ordered) - Optimized async writes
            var writerTask = Task.Run(async () =>
            {
                var pending = new Dictionary<int, (long devOff, int ulen, byte[] hash, byte[] comp)>();
                var headerBuffer = new byte[ImageFormat.ChunkHeaderSize];
                foreach (var item in compBlocks.GetConsumingEnumerable())
                {
                    pending[item.idx] = (item.devOff, item.ulen, item.hash, item.comp);
                    while (pending.TryGetValue(nextWriteIndex, out var w))
                    {
                        long payloadPos = _out.Position + ImageFormat.ChunkHeaderSize;
                        
                        // Write header asynchronously without BinaryWriter
                        var headerSpan = headerBuffer.AsSpan();
                        BitConverter.TryWriteBytes(headerSpan, nextWriteIndex);
                        BitConverter.TryWriteBytes(headerSpan.Slice(4), w.devOff);
                        BitConverter.TryWriteBytes(headerSpan.Slice(12), w.ulen);
                        BitConverter.TryWriteBytes(headerSpan.Slice(16), w.comp.Length);
                        w.hash.CopyTo(headerSpan.Slice(20));
                        
                        await _out.WriteAsync(headerBuffer, 0, ImageFormat.ChunkHeaderSize);
                        await _out.WriteAsync(w.comp, 0, w.comp.Length);
                        
                        _index.Add(new IndexEntry { DeviceOffset = w.devOff, FileOffset = payloadPos, UncompressedLength = w.ulen, CompressedLength = w.comp.Length });
                        nextWriteIndex++;
                        pending.Remove(nextWriteIndex - 1);
                        progress?.Invoke(w.devOff + w.ulen - startDeviceOffset, (maxBytes.HasValue ? Math.Min(reader.TotalSize - startDeviceOffset, maxBytes.Value) : (reader.TotalSize - startDeviceOffset)));
                    }
                }
            });

            // Reader loop (producer)
            while (remaining > 0)
            {
                int toRead = (int)Math.Min(_chunkSize, remaining);
                byte[] buffer = new byte[toRead];
                int read = await reader.ReadAlignedAsync(buffer, 0, toRead);
                if (read <= 0) break;
                if (read != toRead) Array.Resize(ref buffer, read);
                long thisOff = deviceOffset;
                readBlocks.Add((nextIndex, thisOff, buffer));
                nextIndex++;
                deviceOffset += read;
                remaining -= read;
            }
            readBlocks.CompleteAdding();
            // Wait for all current compressor workers to finish processing
            var tasksSnapshot = pool.SnapshotTasks();
            try { await Task.WhenAll(tasksSnapshot); } catch { }
            compBlocks.CompleteAdding();
            await writerTask;
            if (monitorCts != null)
            {
                try { monitorCts.Cancel(); } catch { }
                try { if (monitorTask != null) await monitorTask; } catch { }
            }

            long indexStart = WriteFooter();
            return (_index.Count, deviceOffset);
        }

        // Used-blocks-only imaging: iterate allocated ranges and write chunks only for those ranges.
    public (int chunksWritten, long lastDeviceOffset) WriteAllocatedOnly(IBlockReader reader, Action<long,long>? progress = null, int? parallel = null, Func<int>? getDesiredParallel = null, int pipelineDepth = 2, bool enableAdaptiveConcurrency = false)
        {
            if (!_wroteHeader) WriteHeader();
            // Parallel pipeline for allocated ranges: enumerate -> read -> compress -> write (ordered)
            int workerCount = Math.Max(1, parallel.HasValue ? parallel.Value : Environment.ProcessorCount);
            int bounded = Math.Max(2, workerCount * Math.Max(1, pipelineDepth));
            var readBlocks = new System.Collections.Concurrent.BlockingCollection<(int idx, long devOff, byte[] data)>(bounded);
            var compBlocks = new System.Collections.Concurrent.BlockingCollection<(int idx, long devOff, int ulen, byte[] hash, byte[] comp)>(bounded);

            long total = reader.TotalSize; // keep progress total as logical device size
            long done = 0;
            int nextIndex = _index.Count;
            int nextWriteIndex = _index.Count;
            // Adjustable compressor worker pool
            var pool = new AdjustableCompressorPool(readBlocks, compBlocks, workerCount);
            CancellationTokenSource? monitorCts = null;
            Task? monitorTask = null;
            if (getDesiredParallel != null)
            {
                monitorCts = new CancellationTokenSource();
                monitorTask = Task.Run(async () =>
                {
                    int last = workerCount;
                    while (!monitorCts.IsCancellationRequested)
                    {
                        try
                        {
                            int desired = getDesiredParallel();
                            desired = Math.Max(1, Math.Min(desired, Environment.ProcessorCount * 2));
                            if (desired != last)
                            {
                                pool.SetDegree(desired);
                                last = desired;
                            }
                        }
                        catch { }
                        try { await Task.Delay(1000, monitorCts.Token); } catch { }
                    }
                });
            }

            // Writer (ordered) with optimized async writes for smoother I/O
            var writerTask = Task.Run(async () =>
            {
                var pending = new Dictionary<int, (long devOff, int ulen, byte[] hash, byte[] comp)>();
                var headerBuffer = new byte[ImageFormat.ChunkHeaderSize];
                foreach (var item in compBlocks.GetConsumingEnumerable())
                {
                    pending[item.idx] = (item.devOff, item.ulen, item.hash, item.comp);
                    while (pending.TryGetValue(nextWriteIndex, out var w))
                    {
                        long payloadPos = _out.Position + ImageFormat.ChunkHeaderSize;
                        
                        // Write header asynchronously without BinaryWriter
                        var headerSpan = headerBuffer.AsSpan();
                        BitConverter.TryWriteBytes(headerSpan, nextWriteIndex);
                        BitConverter.TryWriteBytes(headerSpan.Slice(4), w.devOff);
                        BitConverter.TryWriteBytes(headerSpan.Slice(12), w.ulen);
                        BitConverter.TryWriteBytes(headerSpan.Slice(16), w.comp.Length);
                        w.hash.CopyTo(headerSpan.Slice(20));
                        
                        await _out.WriteAsync(headerBuffer, 0, ImageFormat.ChunkHeaderSize);
                        await _out.WriteAsync(w.comp, 0, w.comp.Length);
                        
                        _index.Add(new IndexEntry { DeviceOffset = w.devOff, FileOffset = payloadPos, UncompressedLength = w.ulen, CompressedLength = w.comp.Length });
                        nextWriteIndex++;
                        pending.Remove(nextWriteIndex - 1);
                        done += w.ulen;
                        progress?.Invoke(done, total);
                    }
                }
            });

            // Producer: enumerate allocated ranges and read into chunk-sized buffers
            reader.TryEnumerateNtfsAllocatedRanges((offset, length) =>
            {
                long remaining = length;
                long pos = offset;
                while (remaining > 0)
                {
                    int toRead = (int)Math.Min(_chunkSize, remaining);
                    var buffer = new byte[toRead];
                    reader.Seek(pos);
                    int read = reader.ReadAligned(buffer, 0, toRead);
                    if (read <= 0) break;
                    if (read != toRead) Array.Resize(ref buffer, read);
                    readBlocks.Add((nextIndex, pos, buffer));
                    nextIndex++;
                    pos += read;
                    remaining -= read;
                }
            }, out long _);

            readBlocks.CompleteAdding();
            var tasksSnapshot = pool.SnapshotTasks();
            try { Task.WaitAll(tasksSnapshot.ToArray()); } catch { }
            compBlocks.CompleteAdding();
            writerTask.Wait();

            if (monitorCts != null)
            {
                try { monitorCts.Cancel(); } catch { }
                try { if (monitorTask != null) monitorTask.Wait(); } catch { }
            }

            long indexStart = WriteFooter();
            return (_index.Count, done);
        }

        private long WriteFooter()
        {
            // layout: 'IDX1' [int32 chunkCount] entries... then 'TAIL' [int64 indexStart]
            long indexStart = _out.Position;
            
            // Write index magic and count
            _out.Write(ImageFormat.IndexMagic, 0, ImageFormat.IndexMagic.Length);
            var countBytes = new byte[4];
            BitConverter.TryWriteBytes(countBytes, _index.Count);
            _out.Write(countBytes, 0, countBytes.Length);
            
            // Write index entries
            var entryBuffer = new byte[24]; // 8 + 8 + 4 + 4
            foreach (var e in _index)
            {
                var span = entryBuffer.AsSpan();
                BitConverter.TryWriteBytes(span, e.DeviceOffset);
                BitConverter.TryWriteBytes(span.Slice(8), e.FileOffset);
                BitConverter.TryWriteBytes(span.Slice(16), e.UncompressedLength);
                BitConverter.TryWriteBytes(span.Slice(20), e.CompressedLength);
                _out.Write(entryBuffer, 0, entryBuffer.Length);
            }
            
            // Write tail magic and index start
            _out.Write(ImageFormat.TailMagic, 0, ImageFormat.TailMagic.Length);
            var indexStartBytes = new byte[8];
            BitConverter.TryWriteBytes(indexStartBytes, indexStart);
            _out.Write(indexStartBytes, 0, indexStartBytes.Length);
            
            _out.Flush();
            return indexStart;
        }
    }
}
