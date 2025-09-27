using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ImagingUtility
{
    // Provides random-access reads over a compressed chunked image without full extraction.
    // Thread-safe for concurrent readers; uses a small LRU cache of decompressed chunks.
    internal sealed class RandomAccessImage : IDisposable
    {
        private readonly ImageReader _reader;
        private readonly FileStream _fs;
        private readonly object _lruLock = new object();
        private readonly int _cacheCapacity;
        private readonly Dictionary<int, byte[]> _cache = new();
        private readonly LinkedList<int> _lru = new(); // most-recent at front

        public long Length { get; }

        public RandomAccessImage(string imagePath, int cacheCapacity = 4)
        {
            _reader = new ImageReader(imagePath);
            _fs = new FileStream(imagePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            _cacheCapacity = Math.Max(1, cacheCapacity);
            long len = 0; foreach (var e in _reader.Index) len = Math.Max(len, e.DeviceOffset + e.UncompressedLength);
            Length = len;
        }

        public async Task ReadAsync(long offset, byte[] buffer, int bufferOffset, int count, CancellationToken ct = default)
        {
            if (offset < 0 || count < 0 || bufferOffset < 0 || bufferOffset + count > buffer.Length)
                throw new ArgumentOutOfRangeException();
            if (count == 0) return;
            // Pre-zero the destination span to ensure consistent zero-fill past EOF
            Array.Clear(buffer, bufferOffset, count);
            if (offset >= Length) return; // entirely beyond EOF -> zeros

            long remaining = Math.Min((long)count, Length - offset);
            int destOff = bufferOffset;
            while (remaining > 0)
            {
                // Find chunk for current offset
                int chunkIndex = FindChunkIndex(offset);
                if (chunkIndex < 0) break; // gap or end
                var e = _reader.Index[chunkIndex];
                var chunk = await GetChunkAsync(chunkIndex, ct).ConfigureAwait(false);
                long within = offset - e.DeviceOffset;
                int canCopy = (int)Math.Min(remaining, chunk.Length - within);
                Buffer.BlockCopy(chunk, (int)within, buffer, destOff, canCopy);
                remaining -= canCopy;
                destOff += canCopy;
                offset += canCopy;
            }
        }

        private int FindChunkIndex(long deviceOffset)
        {
            // Binary search over index by DeviceOffset
            int lo = 0, hi = _reader.Index.Count - 1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1;
                var e = _reader.Index[mid];
                long start = e.DeviceOffset;
                long end = start + e.UncompressedLength; // exclusive
                if (deviceOffset < start) hi = mid - 1; else if (deviceOffset >= end) lo = mid + 1; else return mid;
            }
            return -1;
        }

        private async Task<byte[]> GetChunkAsync(int index, CancellationToken ct)
        {
            // Fast path: check cache
            if (TryGetFromCache(index, out var cached)) return cached;
            // Load + decompress
            var e = _reader.Index[index];
            byte[] compressed = ArrayPool<byte>.Shared.Rent(e.CompressedLength);
            try
            {
                _fs.Seek(e.FileOffset, SeekOrigin.Begin);
                int read = 0;
                while (read < e.CompressedLength)
                {
                    int r = await _fs.ReadAsync(compressed, read, e.CompressedLength - read, ct).ConfigureAwait(false);
                    if (r <= 0) throw new IOException("Unexpected EOF reading compressed chunk");
                    read += r;
                }
                using var dec = new ZstdSharp.Decompressor();
                var decompressed = dec.Unwrap(new ReadOnlySpan<byte>(compressed, 0, e.CompressedLength)).ToArray();
                if (decompressed.Length != e.UncompressedLength)
                    throw new IOException("Decompressed length mismatch");
                PutInCache(index, decompressed);
                return decompressed;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(compressed);
            }
        }

        private bool TryGetFromCache(int index, out byte[] bytes)
        {
            lock (_lruLock)
            {
                if (_cache.TryGetValue(index, out bytes!))
                {
                    // move to front
                    var node = _lru.Find(index);
                    if (node != null) { _lru.Remove(node); _lru.AddFirst(node); }
                    return true;
                }
            }
            bytes = Array.Empty<byte>();
            return false;
        }

        private void PutInCache(int index, byte[] bytes)
        {
            lock (_lruLock)
            {
                if (_cache.ContainsKey(index))
                {
                    _cache[index] = bytes; // update
                    var node = _lru.Find(index);
                    if (node != null) { _lru.Remove(node); _lru.AddFirst(node); }
                    else _lru.AddFirst(index);
                    return;
                }
                _cache[index] = bytes;
                _lru.AddFirst(index);
                while (_cache.Count > _cacheCapacity)
                {
                    var last = _lru.Last;
                    if (last == null) break;
                    _cache.Remove(last.Value);
                    _lru.RemoveLast();
                }
            }
        }

        public void Dispose()
        {
            _fs.Dispose();
        }
    }
}
