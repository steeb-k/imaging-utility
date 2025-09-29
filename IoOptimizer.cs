using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ImagingUtility
{
    /// <summary>
    /// I/O optimization utilities for better file and device performance
    /// </summary>
    internal static class IoOptimizer
    {
        /// <summary>
        /// Creates an optimized FileStream with optimal buffer size and flags
        /// </summary>
        public static FileStream CreateOptimizedFileStream(string path, FileMode mode, FileAccess access, FileShare share, bool writeThrough = false)
        {
            var options = FileOptions.SequentialScan | FileOptions.RandomAccess;
            if (writeThrough)
            {
                options |= FileOptions.WriteThrough;
            }
            
            // Calculate optimal buffer size based on system
            int bufferSize = CalculateOptimalBufferSize();
            
            return new FileStream(path, mode, access, share, bufferSize, options);
        }
        
        /// <summary>
        /// Creates an optimized FileStream for device access
        /// </summary>
        public static FileStream CreateOptimizedDeviceStream(SafeFileHandle handle, FileAccess access, bool isAsync = true)
        {
            // Use larger buffer for device access
            int bufferSize = Math.Max(4 * 1024 * 1024, CalculateOptimalBufferSize());
            
            return new FileStream(handle, access, bufferSize, isAsync);
        }
        
        /// <summary>
        /// Calculates optimal buffer size based on system characteristics
        /// </summary>
        private static int CalculateOptimalBufferSize()
        {
            try
            {
                // Get system page size
                var systemInfo = new SYSTEM_INFO();
                GetSystemInfo(ref systemInfo);
                int pageSize = (int)systemInfo.dwPageSize;
                
                // Get available memory
                var memStatus = new MEMORYSTATUSEX();
                memStatus.dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>();
                if (GlobalMemoryStatusEx(ref memStatus))
                {
                    // Use 1% of available memory, but between 64KB and 64MB
                    long availableMemory = (long)memStatus.ullAvailPhys;
                    long optimalSize = Math.Max(64 * 1024, Math.Min(64 * 1024 * 1024, availableMemory / 100));
                    
                    // Round to page boundary
                    return ((int)optimalSize / pageSize) * pageSize;
                }
            }
            catch
            {
                // Fallback to default
            }
            
            return 1024 * 1024; // 1MB default
        }
        
        /// <summary>
        /// Optimizes file alignment for better performance
        /// </summary>
        public static long AlignToOptimalBoundary(long offset, int sectorSize = 4096)
        {
            // Align to 64KB boundary for optimal performance
            const long optimalAlignment = 64 * 1024;
            return (offset / optimalAlignment) * optimalAlignment;
        }
        
        /// <summary>
        /// Gets optimal read size for the given buffer
        /// </summary>
        public static int GetOptimalReadSize(int bufferSize, int sectorSize = 4096)
        {
            // Align to sector boundary and use reasonable chunk size
            int alignedSize = ((bufferSize / sectorSize) * sectorSize);
            return Math.Max(sectorSize, Math.Min(alignedSize, 16 * 1024 * 1024)); // Max 16MB
        }
        
        [DllImport("kernel32.dll")]
        private static extern void GetSystemInfo(ref SYSTEM_INFO lpSystemInfo);
        
        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);
        
        [StructLayout(LayoutKind.Sequential)]
        private struct SYSTEM_INFO
        {
            public uint dwOemId;
            public uint dwProcessorType;
            public uint dwPageSize;
            public IntPtr lpMinimumApplicationAddress;
            public IntPtr lpMaximumApplicationAddress;
            public IntPtr dwActiveProcessorMask;
            public uint dwNumberOfProcessors;
            public uint dwProcessorType2;
            public uint dwAllocationGranularity;
            public ushort wProcessorLevel;
            public ushort wProcessorRevision;
        }
        
        [StructLayout(LayoutKind.Sequential)]
        private struct MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
        }
    }
    
    /// <summary>
    /// Optimized file writer with buffering and alignment
    /// </summary>
    internal class OptimizedFileWriter : IDisposable
    {
        private readonly FileStream _stream;
        private readonly byte[] _buffer;
        private int _bufferPosition = 0;
        private readonly int _sectorSize;
        private bool _disposed = false;
        
        public OptimizedFileWriter(string path, int sectorSize = 4096, bool writeThrough = false)
        {
            _sectorSize = sectorSize;
            _stream = IoOptimizer.CreateOptimizedFileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, writeThrough);
            _buffer = new byte[Math.Max(64 * 1024, sectorSize * 16)]; // At least 64KB buffer
        }
        
        public void Write(byte[] data, int offset, int count)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(OptimizedFileWriter));
            
            int remaining = count;
            int dataOffset = offset;
            
            while (remaining > 0)
            {
                int toCopy = Math.Min(remaining, _buffer.Length - _bufferPosition);
                Array.Copy(data, dataOffset, _buffer, _bufferPosition, toCopy);
                
                _bufferPosition += toCopy;
                dataOffset += toCopy;
                remaining -= toCopy;
                
                // Flush buffer if it's full
                if (_bufferPosition >= _buffer.Length)
                {
                    FlushBuffer();
                }
            }
        }
        
        public async Task WriteAsync(byte[] data, int offset, int count)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(OptimizedFileWriter));
            
            int remaining = count;
            int dataOffset = offset;
            
            while (remaining > 0)
            {
                int toCopy = Math.Min(remaining, _buffer.Length - _bufferPosition);
                Array.Copy(data, dataOffset, _buffer, _bufferPosition, toCopy);
                
                _bufferPosition += toCopy;
                dataOffset += toCopy;
                remaining -= toCopy;
                
                // Flush buffer if it's full
                if (_bufferPosition >= _buffer.Length)
                {
                    await FlushBufferAsync();
                }
            }
        }
        
        private void FlushBuffer()
        {
            if (_bufferPosition > 0)
            {
                _stream.Write(_buffer, 0, _bufferPosition);
                _bufferPosition = 0;
            }
        }
        
        private async Task FlushBufferAsync()
        {
            if (_bufferPosition > 0)
            {
                await _stream.WriteAsync(_buffer, 0, _bufferPosition);
                _bufferPosition = 0;
            }
        }
        
        public void Flush()
        {
            FlushBuffer();
            _stream.Flush();
        }
        
        public async Task FlushAsync()
        {
            await FlushBufferAsync();
            await _stream.FlushAsync();
        }
        
        public void Dispose()
        {
            if (!_disposed)
            {
                Flush();
                _stream.Dispose();
                _disposed = true;
            }
        }
    }
    
    /// <summary>
    /// I/O performance monitor
    /// </summary>
    internal class IoPerformanceMonitor
    {
        private readonly List<IoOperation> _operations = new List<IoOperation>();
        private readonly object _lock = new object();
        private readonly int _maxOperations = 100;
        
        public struct IoOperation
        {
            public DateTime Timestamp;
            public IoOperationType Type;
            public long Size;
            public double DurationMs;
            public double ThroughputMBps;
        }
        
        public enum IoOperationType
        {
            Read,
            Write,
            Seek
        }
        
        public void RecordOperation(IoOperationType type, long size, double durationMs)
        {
            var operation = new IoOperation
            {
                Timestamp = DateTime.UtcNow,
                Type = type,
                Size = size,
                DurationMs = durationMs,
                ThroughputMBps = (size / (1024.0 * 1024.0)) / (durationMs / 1000.0)
            };
            
            lock (_lock)
            {
                _operations.Add(operation);
                if (_operations.Count > _maxOperations)
                {
                    _operations.RemoveAt(0);
                }
            }
        }
        
        public IoStats GetStats()
        {
            lock (_lock)
            {
                if (_operations.Count == 0)
                {
                    return new IoStats();
                }
                
                var readOps = _operations.Where(o => o.Type == IoOperationType.Read).ToList();
                var writeOps = _operations.Where(o => o.Type == IoOperationType.Write).ToList();
                
                return new IoStats
                {
                    TotalOperations = _operations.Count,
                    ReadOperations = readOps.Count,
                    WriteOperations = writeOps.Count,
                    AverageReadThroughputMBps = readOps.Count > 0 ? readOps.Average(o => o.ThroughputMBps) : 0,
                    AverageWriteThroughputMBps = writeOps.Count > 0 ? writeOps.Average(o => o.ThroughputMBps) : 0,
                    AverageOperationDurationMs = _operations.Average(o => o.DurationMs)
                };
            }
        }
    }
    
    /// <summary>
    /// I/O performance statistics
    /// </summary>
    public struct IoStats
    {
        public int TotalOperations;
        public int ReadOperations;
        public int WriteOperations;
        public double AverageReadThroughputMBps;
        public double AverageWriteThroughputMBps;
        public double AverageOperationDurationMs;
    }
}
