using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace ImagingUtility
{
    /// <summary>
    /// Enhanced device reader with read-ahead, larger aligned reads, and prefetch optimization
    /// </summary>
    internal class OptimizedDeviceReader : IBlockReader
    {
        private readonly FileStream _stream;
        private readonly SafeFileHandle _handle;
        private readonly int _sectorSize;
        private readonly long _totalSize;
        private readonly int _readAheadBufferSize;
        private readonly int _maxReadAheadBuffers;
        
        // Read-ahead cache
        private readonly ConcurrentDictionary<long, byte[]> _readAheadCache = new();
        private readonly SemaphoreSlim _cacheSemaphore;
        private readonly Task? _prefetchTask;
        private readonly CancellationTokenSource _cancellationTokenSource;
        private long _currentPosition = 0;
        private readonly object _positionLock = new object();
        private bool _volumeLocked = false;
        
        public long TotalSize => _totalSize;
        public int SectorSize => _sectorSize;
        public long Position => _currentPosition;
        
        public OptimizedDeviceReader(string devicePath, int readAheadBufferSize = 16 * 1024 * 1024, int maxReadAheadBuffers = 4)
        {
            _readAheadBufferSize = readAheadBufferSize;
            _maxReadAheadBuffers = maxReadAheadBuffers;
            _cacheSemaphore = new SemaphoreSlim(maxReadAheadBuffers, maxReadAheadBuffers);
            _cancellationTokenSource = new CancellationTokenSource();
            
            // Open device with optimized flags
            _handle = NativeMethods.CreateFile(devicePath,
                NativeMethods.GENERIC_READ,
                NativeMethods.FILE_SHARE_READ | NativeMethods.FILE_SHARE_WRITE | NativeMethods.FILE_SHARE_DELETE,
                IntPtr.Zero,
                NativeMethods.OPEN_EXISTING,
                NativeMethods.FILE_ATTRIBUTE_NORMAL | NativeMethods.FILE_FLAG_SEQUENTIAL_SCAN,
                IntPtr.Zero);

            if (_handle.IsInvalid)
                throw new IOException($"Failed to open device {devicePath}");

            // Check if this is a device that doesn't support overlapped I/O
            bool isVssSnapshot = devicePath.Contains("HarddiskVolumeShadowCopy");
            bool isMountedDrive = devicePath.StartsWith(@"\\.\") && devicePath.EndsWith(":");
            bool isPhysicalDrive = devicePath.StartsWith(@"\\.\PhysicalDrive");
            bool isVolumeGuid = devicePath.StartsWith(@"\\?\Volume{") && devicePath.EndsWith("}");
            
            // Try aggressive locking for mounted drives to enable async I/O
            bool supportsAsync = !isVssSnapshot && !isPhysicalDrive && !isVolumeGuid;
            if (isMountedDrive)
            {
                Console.WriteLine($"Attempting aggressive locking for device: {devicePath}");
                
                // Step 1: Try exclusive access with overlapped I/O
                Console.WriteLine("Step 1: Attempting exclusive file access...");
                var exclusiveHandle = NativeMethods.CreateFile(devicePath,
                    NativeMethods.GENERIC_READ,
                    NativeMethods.FILE_SHARE_NONE,  // Exclusive access
                    IntPtr.Zero,
                    NativeMethods.OPEN_EXISTING,
                    NativeMethods.FILE_ATTRIBUTE_NORMAL | NativeMethods.FILE_FLAG_OVERLAPPED,
                    IntPtr.Zero);
                
                if (!exclusiveHandle.IsInvalid)
                {
                    Console.WriteLine("✓ Exclusive file access successful");
                    
                    // Step 2: Try to lock the volume
                    Console.WriteLine("Step 2: Attempting volume lock...");
                    bool volumeLocked = false;
                    try
                    {
                        int lockBytes = 0;
                        volumeLocked = NativeMethods.DeviceIoControl(exclusiveHandle, 
                            NativeMethods.FSCTL_LOCK_VOLUME, 
                            IntPtr.Zero, 0, IntPtr.Zero, 0, out lockBytes, IntPtr.Zero);
                        
                        if (volumeLocked)
                        {
                            Console.WriteLine("✓ Volume lock successful");
                        }
                        else
                        {
                            Console.WriteLine($"✗ Volume lock failed (Error: {Marshal.GetLastWin32Error()})");
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"✗ Volume lock exception: {ex.Message}");
                        volumeLocked = false;
                    }
                    
                    if (volumeLocked)
                    {
                        _volumeLocked = true;
                        // Step 3: Try to dismount the volume (most aggressive)
                        Console.WriteLine("Step 3: Attempting volume dismount...");
                        try
                        {
                            int dismountBytes = 0;
                            bool dismounted = NativeMethods.DeviceIoControl(exclusiveHandle, 
                                NativeMethods.FSCTL_DISMOUNT_VOLUME, 
                                IntPtr.Zero, 0, IntPtr.Zero, 0, out dismountBytes, IntPtr.Zero);
                            
                            if (dismounted)
                            {
                                Console.WriteLine("✓ Volume dismount successful - FULL CONTROL ACHIEVED!");
                                // We have full control - use exclusive handle with async I/O
                                _handle.Dispose();
                                _handle = exclusiveHandle;
                                supportsAsync = true;
                            }
                            else
                            {
                                Console.WriteLine($"✗ Volume dismount failed (Error: {Marshal.GetLastWin32Error()}) - but we have the lock");
                                // Couldn't dismount, but we have the lock - still try async
                                _handle.Dispose();
                                _handle = exclusiveHandle;
                                supportsAsync = true;
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"✗ Volume dismount exception: {ex.Message} - but we have the lock");
                            // Dismount failed, but we still have the lock
                            _handle.Dispose();
                            _handle = exclusiveHandle;
                            supportsAsync = true;
                        }
                    }
                    else
                    {
                        Console.WriteLine("⚠ Couldn't lock volume, but we have exclusive access - trying async anyway");
                        // Couldn't lock volume, but we have exclusive access - try async anyway
                        _handle.Dispose();
                        _handle = exclusiveHandle;
                        supportsAsync = true;
                    }
                }
                else
                {
                    Console.WriteLine($"✗ Exclusive file access failed (Error: {Marshal.GetLastWin32Error()}) - falling back to shared access");
                    // Couldn't get exclusive access - fall back to shared with sync I/O
                    supportsAsync = false;
                }
            }
            
            // Use larger buffer size for better throughput, but disable async for devices that don't support it
            _stream = new FileStream(_handle, FileAccess.Read, bufferSize: Math.Max(4 * 1024 * 1024, readAheadBufferSize), isAsync: supportsAsync);
            
            if (isMountedDrive || isPhysicalDrive)
            {
                Console.WriteLine($"Final result: Async I/O {(supportsAsync ? "ENABLED" : "DISABLED")} for {devicePath}");
                if (isPhysicalDrive)
                {
                    Console.WriteLine("Note: Physical drives don't support overlapped I/O - using synchronous I/O with optimized buffering");
                }
            }
            
            // Get device geometry
            var geo = new NativeMethods.DISK_GEOMETRY_EX();
            int bytes = 0;
            bool ok = NativeMethods.DeviceIoControl(_handle, NativeMethods.IOCTL_DISK_GET_DRIVE_GEOMETRY_EX,
                IntPtr.Zero, 0,
                ref geo, Marshal.SizeOf<NativeMethods.DISK_GEOMETRY_EX>(), out bytes, IntPtr.Zero);

            if (ok)
            {
                _totalSize = (long)geo.DiskSize;
                _sectorSize = (int)geo.Geometry.BytesPerSector;
            }
            else
            {
                // Fallback to length info
                var lenInfo = new NativeMethods.GET_LENGTH_INFORMATION();
                bytes = 0;
                bool okLen = NativeMethods.DeviceIoControl(_handle, NativeMethods.IOCTL_DISK_GET_LENGTH_INFO,
                    IntPtr.Zero, 0,
                    ref lenInfo, Marshal.SizeOf<NativeMethods.GET_LENGTH_INFORMATION>(), out bytes, IntPtr.Zero);
                if (okLen)
                {
                    _totalSize = lenInfo.Length;
                    _sectorSize = 4096; // Safe default
                }
                else
                {
                    throw new IOException("Failed to query device size");
                }
            }
            
            // Start prefetch task
            // Only start prefetch task for devices that support async operations
            if (supportsAsync)
            {
                _prefetchTask = Task.Run(PrefetchLoop);
            }
        }
        
        public void Seek(long offset)
        {
            lock (_positionLock)
            {
                _currentPosition = offset;
                _stream.Seek(offset, SeekOrigin.Begin);
            }
        }
        
        public int ReadAligned(byte[] buffer, int offset, int count)
        {
            // Align read size to sector boundary for optimal performance
            int alignedCount = AlignToSector(count);
            if (alignedCount > count) alignedCount = count;
            
            // Try to get from read-ahead cache first
            long cacheKey = (_currentPosition / _readAheadBufferSize) * _readAheadBufferSize;
            if (_readAheadCache.TryGetValue(cacheKey, out byte[]? cachedData))
            {
                int cacheOffset = (int)(_currentPosition - cacheKey);
                int toCopy = Math.Min(alignedCount, cachedData.Length - cacheOffset);
                if (toCopy > 0)
                {
                    Array.Copy(cachedData, cacheOffset, buffer, offset, toCopy);
                    _currentPosition += toCopy;
                    return toCopy;
                }
            }
            
            // Fallback to direct read
            int read = _stream.Read(buffer, offset, alignedCount);
            _currentPosition += read;
            return read;
        }
        
        public async Task<int> ReadAlignedAsync(byte[] buffer, int offset, int count)
        {
            var totalStopwatch = System.Diagnostics.Stopwatch.StartNew();
            
            // Align read size to sector boundary
            int alignedCount = AlignToSector(count);
            if (alignedCount > count) alignedCount = count;
            
            // Try to get from read-ahead cache first
            long cacheKey = (_currentPosition / _readAheadBufferSize) * _readAheadBufferSize;
            if (_readAheadCache.TryGetValue(cacheKey, out byte[]? cachedData))
            {
                int cacheOffset = (int)(_currentPosition - cacheKey);
                int toCopy = Math.Min(alignedCount, cachedData.Length - cacheOffset);
                if (toCopy > 0)
                {
                    Array.Copy(cachedData, cacheOffset, buffer, offset, toCopy);
                    _currentPosition += toCopy;
                    
                    totalStopwatch.Stop();
                    if (totalStopwatch.ElapsedMilliseconds > 5)
                    {
                        Console.WriteLine($"Cache hit: {toCopy} bytes in {totalStopwatch.ElapsedMilliseconds}ms");
                    }
                    
                    return toCopy;
                }
            }
            
            // Fallback to direct async read
            var readStopwatch = System.Diagnostics.Stopwatch.StartNew();
            int read = await _stream.ReadAsync(buffer, offset, alignedCount);
            readStopwatch.Stop();
            
            _currentPosition += read;
            
            totalStopwatch.Stop();
            
            // Log performance metrics for slow reads
            if (readStopwatch.ElapsedMilliseconds > 100 || totalStopwatch.ElapsedMilliseconds > 150)
            {
                var mbps = (read / 1024.0 / 1024.0) / (readStopwatch.ElapsedMilliseconds / 1000.0);
                Console.WriteLine($"Read {read} bytes in {readStopwatch.ElapsedMilliseconds}ms ({mbps:F1} MB/s) - Total: {totalStopwatch.ElapsedMilliseconds}ms");
            }
            
            return read;
        }
        
        private int AlignToSector(int count)
        {
            // Round up to nearest sector boundary
            return ((count + _sectorSize - 1) / _sectorSize) * _sectorSize;
        }
        
        private async Task PrefetchLoop()
        {
            while (!_cancellationTokenSource.Token.IsCancellationRequested)
            {
                try
                {
                    await _cacheSemaphore.WaitAsync(_cancellationTokenSource.Token);
                    
                    long currentPos;
                    lock (_positionLock)
                    {
                        currentPos = _currentPosition;
                    }
                    
                    // Calculate next read-ahead position
                    long prefetchPos = (currentPos / _readAheadBufferSize + 1) * _readAheadBufferSize;
                    
                    if (prefetchPos >= _totalSize)
                    {
                        _cacheSemaphore.Release();
                        await Task.Delay(100, _cancellationTokenSource.Token);
                        continue;
                    }
                    
                    // Check if already cached
                    if (_readAheadCache.ContainsKey(prefetchPos))
                    {
                        _cacheSemaphore.Release();
                        await Task.Delay(50, _cancellationTokenSource.Token);
                        continue;
                    }
                    
                    // Perform read-ahead
                    await PrefetchData(prefetchPos);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch
                {
                    // Continue on errors
                }
            }
        }
        
        private async Task PrefetchData(long position)
        {
            try
            {
                var buffer = new byte[_readAheadBufferSize];
                long originalPos = _stream.Position;
                
                _stream.Seek(position, SeekOrigin.Begin);
                int read = await _stream.ReadAsync(buffer, 0, _readAheadBufferSize);
                _stream.Seek(originalPos, SeekOrigin.Begin);
                
                if (read > 0)
                {
                    // Trim buffer to actual read size
                    if (read < buffer.Length)
                    {
                        var trimmedBuffer = new byte[read];
                        Array.Copy(buffer, 0, trimmedBuffer, 0, read);
                        buffer = trimmedBuffer;
                    }
                    
                    _readAheadCache[position] = buffer;
                    
                    // Clean up old cache entries if we have too many
                    if (_readAheadCache.Count > _maxReadAheadBuffers)
                    {
                        var keysToRemove = new List<long>();
                        foreach (var kvp in _readAheadCache)
                        {
                            if (Math.Abs(kvp.Key - position) > _readAheadBufferSize * _maxReadAheadBuffers)
                            {
                                keysToRemove.Add(kvp.Key);
                            }
                        }
                        
                        foreach (var key in keysToRemove)
                        {
                            _readAheadCache.TryRemove(key, out _);
                        }
                    }
                }
            }
            finally
            {
                _cacheSemaphore.Release();
            }
        }
        
        public bool TryGetNtfsBytesPerCluster(out int bytesPerCluster)
        {
            bytesPerCluster = 0;
            if (_handle == null || _handle.IsInvalid) return false;
            
            int bytes;
            NativeMethods.NTFS_VOLUME_DATA_BUFFER data;
            bool ok = NativeMethods.DeviceIoControl(_handle, NativeMethods.FSCTL_GET_NTFS_VOLUME_DATA,
                IntPtr.Zero, 0,
                out data, Marshal.SizeOf<NativeMethods.NTFS_VOLUME_DATA_BUFFER>(), out bytes, IntPtr.Zero);
            
            if (!ok) return false;
            if (data.BytesPerCluster <= 0) return false;
            
            bytesPerCluster = (int)data.BytesPerCluster;
            return true;
        }
        
        public bool TryEnumerateNtfsAllocatedRanges(Action<long, long> onRange, out long totalAllocatedBytes)
        {
            totalAllocatedBytes = 0;
            if (_handle == null || _handle.IsInvalid) return false;
            if (!TryGetNtfsBytesPerCluster(out int bytesPerCluster)) return false;

            long startLcn = 0;
            const int OutBufSize = 2 * 1024 * 1024; // 2 MiB bitmap buffer for better performance
            byte[] outBuf = new byte[OutBufSize];
            
            while (true)
            {
                var inBuf = new NativeMethods.STARTING_LCN_INPUT_BUFFER { StartingLcn = startLcn };
                int bytesReturned = 0;
                bool ok;
                int err;
                
                ok = NativeMethods.DeviceIoControl(_handle, NativeMethods.FSCTL_GET_VOLUME_BITMAP,
                    ref inBuf, Marshal.SizeOf<NativeMethods.STARTING_LCN_INPUT_BUFFER>(),
                    outBuf, outBuf.Length, out bytesReturned, IntPtr.Zero);
                err = Marshal.GetLastWin32Error();
                
                if (!ok && bytesReturned <= 0)
                {
                    if (err != NativeMethods.ERROR_MORE_DATA) return false;
                }
                if (bytesReturned < 16)
                {
                    if (err == NativeMethods.ERROR_MORE_DATA)
                    {
                        startLcn += 1024 * 8;
                        continue;
                    }
                    break;
                }

                // Parse VOLUME_BITMAP_BUFFER header
                long returnedStartingLcn = BitConverter.ToInt64(outBuf, 0);
                long bitmapSizeInClusters = BitConverter.ToInt64(outBuf, 8);
                int bitmapBytes = bytesReturned - 16;
                if (bitmapBytes < 0) bitmapBytes = 0;

                // Walk bits and coalesce contiguous allocated clusters into ranges
                long currentRunStartCluster = -1;
                long clustersProcessed = 0;
                for (int byteIndex = 16; byteIndex < 16 + bitmapBytes; byteIndex++)
                {
                    byte b = outBuf[byteIndex];
                    for (int bit = 0; bit < 8; bit++)
                    {
                        bool allocated = ((b >> bit) & 1) != 0;
                        if (clustersProcessed >= bitmapSizeInClusters) break;
                        long thisCluster = returnedStartingLcn + clustersProcessed;
                        if (allocated)
                        {
                            if (currentRunStartCluster < 0)
                                currentRunStartCluster = thisCluster;
                        }
                        else
                        {
                            if (currentRunStartCluster >= 0)
                            {
                                long runLenClusters = thisCluster - currentRunStartCluster;
                                long offset = currentRunStartCluster * (long)bytesPerCluster;
                                long length = runLenClusters * (long)bytesPerCluster;
                                if (length > 0)
                                {
                                    onRange(offset, length);
                                    totalAllocatedBytes += length;
                                }
                                currentRunStartCluster = -1;
                            }
                        }
                        clustersProcessed++;
                    }
                }
                
                // Flush trailing run
                if (currentRunStartCluster >= 0)
                {
                    long endCluster = returnedStartingLcn + bitmapSizeInClusters;
                    long runLenClusters = endCluster - currentRunStartCluster;
                    long offset = currentRunStartCluster * (long)bytesPerCluster;
                    long length = runLenClusters * (long)bytesPerCluster;
                    if (length > 0)
                    {
                        onRange(offset, length);
                        totalAllocatedBytes += length;
                    }
                    currentRunStartCluster = -1;
                }

                // Decide whether to continue
                long nextLcn = returnedStartingLcn + bitmapSizeInClusters;
                if (ok)
                {
                    if (bitmapSizeInClusters == 0) break;
                    startLcn = nextLcn;
                    if (startLcn * (long)bytesPerCluster >= _totalSize) break;
                }
                else
                {
                    if (err == NativeMethods.ERROR_MORE_DATA)
                    {
                        startLcn = nextLcn;
                        if (startLcn * (long)bytesPerCluster >= _totalSize) break;
                        continue;
                    }
                    break;
                }
            }

            return true;
        }
        
        public void Dispose()
        {
            _cancellationTokenSource.Cancel();
            try { _prefetchTask.Wait(TimeSpan.FromSeconds(2)); } catch { }
            
            // Unlock volume if we locked it
            if (_volumeLocked && !_handle.IsInvalid)
            {
                try
                {
                    int unlockBytes = 0;
                    NativeMethods.DeviceIoControl(_handle, 
                        NativeMethods.FSCTL_UNLOCK_VOLUME, 
                        IntPtr.Zero, 0, IntPtr.Zero, 0, out unlockBytes, IntPtr.Zero);
                }
                catch
                {
                    // Ignore unlock errors
                }
            }
            
            _cancellationTokenSource.Dispose();
            _cacheSemaphore.Dispose();
            _stream?.Dispose();
            _handle?.Dispose();
        }
    }
}
