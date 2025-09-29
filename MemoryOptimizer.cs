using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime;
using System.Threading;

namespace ImagingUtility
{
    /// <summary>
    /// Memory optimization utilities for reducing allocations and improving GC patterns
    /// </summary>
    internal static class MemoryOptimizer
    {
        private static readonly ArrayPool<byte> _byteArrayPool = ArrayPool<byte>.Create();
        private static readonly ConcurrentQueue<byte[]> _bufferPool = new();
        private static readonly object _gcLock = new object();
        private static DateTime _lastGcTime = DateTime.MinValue;
        private static readonly TimeSpan _gcInterval = TimeSpan.FromSeconds(30);
        
        /// <summary>
        /// Gets a buffer from the pool or creates a new one
        /// </summary>
        public static byte[] RentBuffer(int minimumSize)
        {
            // Try to get from pool first
            if (_bufferPool.TryDequeue(out byte[]? pooledBuffer) && pooledBuffer.Length >= minimumSize)
            {
                return pooledBuffer;
            }
            
            // Rent from ArrayPool as fallback
            return _byteArrayPool.Rent(minimumSize);
        }
        
        /// <summary>
        /// Returns a buffer to the pool for reuse
        /// </summary>
        public static void ReturnBuffer(byte[] buffer)
        {
            if (buffer == null) return;
            
            // Only pool buffers of reasonable size to avoid memory bloat
            if (buffer.Length >= 1024 && buffer.Length <= 64 * 1024 * 1024)
            {
                _bufferPool.Enqueue(buffer);
            }
            else
            {
                _byteArrayPool.Return(buffer, clearArray: true);
            }
        }
        
        /// <summary>
        /// Triggers garbage collection if needed based on memory pressure
        /// </summary>
        public static void TriggerGcIfNeeded()
        {
            lock (_gcLock)
            {
                var now = DateTime.UtcNow;
                if (now - _lastGcTime < _gcInterval) return;
                
                var memoryBefore = GC.GetTotalMemory(false);
                var gen0Collections = GC.CollectionCount(0);
                var gen1Collections = GC.CollectionCount(1);
                var gen2Collections = GC.CollectionCount(2);
                
                // Check if we need to trigger GC
                bool shouldTriggerGc = false;
                
                // Trigger if we have high memory pressure
                if (memoryBefore > GetAvailableMemory() * 0.8)
                {
                    shouldTriggerGc = true;
                }
                
                // Trigger if we have many allocations without collections
                if (gen0Collections == 0 && gen1Collections == 0 && gen2Collections == 0)
                {
                    shouldTriggerGc = true;
                }
                
                if (shouldTriggerGc)
                {
                    GC.Collect(1, GCCollectionMode.Optimized);
                    _lastGcTime = now;
                }
            }
        }
        
        /// <summary>
        /// Gets available system memory
        /// </summary>
        private static long GetAvailableMemory()
        {
            try
            {
                var memStatus = new Program.MEMORYSTATUSEX();
                if (Program.GlobalMemoryStatusEx(memStatus))
                {
                    return (long)memStatus.ullAvailPhys;
                }
            }
            catch { }
            return 8L * 1024 * 1024 * 1024; // Assume 8GB if we can't determine
        }
        
        /// <summary>
        /// Clears the buffer pool to free memory
        /// </summary>
        public static void ClearBufferPool()
        {
            while (_bufferPool.TryDequeue(out _)) { }
        }
        
        /// <summary>
        /// Gets memory usage statistics
        /// </summary>
        public static MemoryStats GetMemoryStats()
        {
            return new MemoryStats
            {
                TotalMemory = GC.GetTotalMemory(false),
                Gen0Collections = GC.CollectionCount(0),
                Gen1Collections = GC.CollectionCount(1),
                Gen2Collections = GC.CollectionCount(2),
                PooledBuffers = _bufferPool.Count,
                AvailableMemory = GetAvailableMemory()
            };
        }
    }
    
    /// <summary>
    /// Memory usage statistics
    /// </summary>
    public struct MemoryStats
    {
        public long TotalMemory;
        public int Gen0Collections;
        public int Gen1Collections;
        public int Gen2Collections;
        public int PooledBuffers;
        public long AvailableMemory;
        
        public double MemoryUsagePercent => (double)TotalMemory / AvailableMemory * 100;
    }
    
    /// <summary>
    /// Optimized buffer manager for imaging operations
    /// </summary>
    internal class OptimizedBufferManager : IDisposable
    {
        private readonly Queue<byte[]> _availableBuffers = new();
        private readonly object _lock = new object();
        private readonly int _bufferSize;
        private readonly int _maxBuffers;
        private int _rentedBuffers = 0;
        
        public OptimizedBufferManager(int bufferSize, int maxBuffers = 10)
        {
            _bufferSize = bufferSize;
            _maxBuffers = maxBuffers;
        }
        
        public byte[] RentBuffer()
        {
            lock (_lock)
            {
                if (_availableBuffers.Count > 0)
                {
                    var buffer = _availableBuffers.Dequeue();
                    _rentedBuffers++;
                    return buffer;
                }
                
                if (_rentedBuffers < _maxBuffers)
                {
                    _rentedBuffers++;
                    return new byte[_bufferSize];
                }
                
                // Wait for a buffer to become available
                while (_availableBuffers.Count == 0)
                {
                    Monitor.Wait(_lock);
                }
                
                var rentedBuffer = _availableBuffers.Dequeue();
                _rentedBuffers++;
                return rentedBuffer;
            }
        }
        
        public void ReturnBuffer(byte[] buffer)
        {
            if (buffer == null || buffer.Length != _bufferSize) return;
            
            lock (_lock)
            {
                _availableBuffers.Enqueue(buffer);
                _rentedBuffers--;
                Monitor.Pulse(_lock);
            }
        }
        
        public void Dispose()
        {
            lock (_lock)
            {
                _availableBuffers.Clear();
            }
        }
    }
    
    /// <summary>
    /// Memory-aware chunk size calculator
    /// </summary>
    internal static class ChunkSizeCalculator
    {
        public static int CalculateOptimalChunkSize(int parallel, int pipelineDepth, long availableMemory)
        {
            const long preferredChunkSize = 512L * 1024 * 1024; // 512 MiB
            const long minChunkSize = 64L * 1024 * 1024;  // 64 MiB
            const long maxChunkSize = 2L * 1024 * 1024 * 1024; // 2 GiB
            
            // Calculate memory needed for the pipeline
            int totalWorkers = parallel * pipelineDepth;
            long memoryNeeded = totalWorkers * preferredChunkSize;
            
            // Add overhead for compression, buffers, etc.
            memoryNeeded = (long)(memoryNeeded * 1.5);
            
            // Reserve 20% of available memory for system
            long usableMemory = (long)(availableMemory * 0.8);
            
            if (memoryNeeded <= usableMemory)
            {
                return (int)Math.Min(preferredChunkSize, maxChunkSize);
            }
            
            // Calculate chunk size that fits in available memory
            long calculatedChunkSize = usableMemory / totalWorkers;
            calculatedChunkSize = Math.Max(calculatedChunkSize, minChunkSize);
            calculatedChunkSize = Math.Min(calculatedChunkSize, maxChunkSize);
            
            return (int)calculatedChunkSize;
        }
        
        public static int CalculateOptimalChunkSize(int parallel, int pipelineDepth)
        {
            long availableMemory = GetAvailableMemory();
            return CalculateOptimalChunkSize(parallel, pipelineDepth, availableMemory);
        }
        
        private static long GetAvailableMemory()
        {
            try
            {
                var memStatus = new Program.MEMORYSTATUSEX();
                if (Program.GlobalMemoryStatusEx(memStatus))
                {
                    return (long)memStatus.ullAvailPhys;
                }
            }
            catch { }
            return 8L * 1024 * 1024 * 1024; // Assume 8GB if we can't determine
        }
    }
}
