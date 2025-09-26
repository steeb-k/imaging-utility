using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ImagingUtility
{
    internal interface IBlockReader : IDisposable
    {
        long TotalSize { get; }
        int SectorSize { get; }
        long Position { get; }
        void Seek(long offset);
        int ReadAligned(byte[] buffer, int offset, int count);
        System.Threading.Tasks.Task<int> ReadAlignedAsync(byte[] buffer, int offset, int count);
        bool TryGetNtfsBytesPerCluster(out int bytesPerCluster);
        bool TryEnumerateNtfsAllocatedRanges(Action<long, long> onRange, out long totalAllocatedBytes);
    }
    internal static class DriveLister
    {
        public static void ListPhysicalDrives()
        {
            Console.WriteLine("Physical drives:");
            for (int i = 0; i < 16; i++)
            {
                var path = $"\\.\\PhysicalDrive{i}";
                try
                {
                    using var r = new RawDeviceReader(path);
                    Console.WriteLine($"  {path} - Size={r.TotalSize} bytes, Sector={r.SectorSize}");
                }
                catch (Exception)
                {
                    // ignore missing/nonexistent drives
                }
            }
        }
    }

    internal class RawDeviceReader : IDisposable, IBlockReader
    {
        private FileStream? _stream;
        private Microsoft.Win32.SafeHandles.SafeFileHandle? _handle;
        public long TotalSize { get; private set; }
        public int SectorSize { get; private set; }

        public RawDeviceReader(string devicePath)
        {
            // open for read
            var handle = NativeMethods.CreateFile(devicePath,
                NativeMethods.GENERIC_READ,
                NativeMethods.FILE_SHARE_READ | NativeMethods.FILE_SHARE_WRITE | NativeMethods.FILE_SHARE_DELETE,
                IntPtr.Zero,
                NativeMethods.OPEN_EXISTING,
                NativeMethods.FILE_ATTRIBUTE_NORMAL | NativeMethods.FILE_FLAG_SEQUENTIAL_SCAN,
                IntPtr.Zero);

            if (handle.IsInvalid)
                throw new IOException($"Failed to open device {devicePath}");

            _handle = handle;
            // Use synchronous FileStream; ReadAsync will be emulated if the handle is non-overlapped.
            _stream = new FileStream(handle, FileAccess.Read, bufferSize: 1024 * 1024, isAsync: false);

            // Try to get geometry first
            var geo = new NativeMethods.DISK_GEOMETRY_EX();
            int bytes = 0;
            bool ok = NativeMethods.DeviceIoControl(handle, NativeMethods.IOCTL_DISK_GET_DRIVE_GEOMETRY_EX,
                IntPtr.Zero, 0,
                ref geo, Marshal.SizeOf<NativeMethods.DISK_GEOMETRY_EX>(), out bytes, IntPtr.Zero);

            if (ok)
            {
                TotalSize = (long)geo.DiskSize;
                SectorSize = (int)geo.Geometry.BytesPerSector;
            }
            else
            {
                // Geometry not available (e.g., snapshot volume). Try length info.
                var lenInfo = new NativeMethods.GET_LENGTH_INFORMATION();
                bytes = 0;
                bool okLen = NativeMethods.DeviceIoControl(handle, NativeMethods.IOCTL_DISK_GET_LENGTH_INFO,
                    IntPtr.Zero, 0,
                    ref lenInfo, Marshal.SizeOf<NativeMethods.GET_LENGTH_INFORMATION>(), out bytes, IntPtr.Zero);
                if (okLen)
                {
                    TotalSize = lenInfo.Length;
                    // Default sector size when unknown; 4096 is safe alignment for 512/4096
                    SectorSize = 4096;
                }
                else
                {
                    throw new IOException("Failed to query device size via IOCTL_DISK_GET_LENGTH_INFO.");
                }
            }
        }

        public void Dispose()
        {
            _stream?.Dispose();
            _stream = null;
            _handle = null;
        }

        public long Position => _stream!.Position;

        public void Seek(long offset)
        {
            _stream!.Seek(offset, SeekOrigin.Begin);
        }

        public int ReadAligned(byte[] buffer, int offset, int count)
        {
            return _stream!.Read(buffer, offset, count);
        }

        public Task<int> ReadAlignedAsync(byte[] buffer, int offset, int count)
        {
            return _stream!.ReadAsync(buffer, offset, count);
        }

        public Microsoft.Win32.SafeHandles.SafeFileHandle? Handle => _handle;

        // Try to get NTFS bytes-per-cluster for this volume. Returns false if not NTFS or not supported.
        public bool TryGetNtfsBytesPerCluster(out int bytesPerCluster)
        {
            bytesPerCluster = 0;
            if (_handle == null || _handle.IsInvalid) return false;
#pragma warning disable CA1416
            int bytes;
            NativeMethods.NTFS_VOLUME_DATA_BUFFER data;
            bool ok = NativeMethods.DeviceIoControl(_handle, NativeMethods.FSCTL_GET_NTFS_VOLUME_DATA,
                IntPtr.Zero, 0,
                out data, Marshal.SizeOf<NativeMethods.NTFS_VOLUME_DATA_BUFFER>(), out bytes, IntPtr.Zero);
#pragma warning restore CA1416
            if (!ok) return false;
            // Basic sanity
            if (data.BytesPerCluster <= 0) return false;
            bytesPerCluster = (int)data.BytesPerCluster;
            return true;
        }

        // Enumerate allocated ranges (offset, length in bytes) using FSCTL_GET_VOLUME_BITMAP on NTFS volumes.
        // Returns false if enumeration is not supported; out totalAllocatedBytes is sum of yielded lengths.
        public bool TryEnumerateNtfsAllocatedRanges(Action<long, long> onRange, out long totalAllocatedBytes)
        {
            totalAllocatedBytes = 0;
            if (_handle == null || _handle.IsInvalid) return false;
            if (!TryGetNtfsBytesPerCluster(out int bytesPerCluster)) return false;

            long startLcn = 0;
            const int OutBufSize = 1 * 1024 * 1024; // 1 MiB bitmap buffer per call
            byte[] outBuf = new byte[OutBufSize];
            while (true)
            {
                var inBuf = new NativeMethods.STARTING_LCN_INPUT_BUFFER { StartingLcn = startLcn };
                int bytesReturned = 0;
                bool ok;
                int err;
#pragma warning disable CA1416
                ok = NativeMethods.DeviceIoControl(_handle, NativeMethods.FSCTL_GET_VOLUME_BITMAP,
                    ref inBuf, Marshal.SizeOf<NativeMethods.STARTING_LCN_INPUT_BUFFER>(),
                    outBuf, outBuf.Length, out bytesReturned, IntPtr.Zero);
                err = Marshal.GetLastWin32Error();
#pragma warning restore CA1416
                if (!ok && bytesReturned <= 0)
                {
                    // If no data returned and call failed with anything other than MORE_DATA, stop.
                    if (err != NativeMethods.ERROR_MORE_DATA) return false;
                }
                if (bytesReturned < 16)
                {
                    // Not enough to contain header
                    if (err == NativeMethods.ERROR_MORE_DATA)
                    {
                        // Advance and continue
                        startLcn += 1024 * 8; // arbitrary skip to avoid tight loop
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
                    if (startLcn * (long)bytesPerCluster >= TotalSize) break;
                }
                else
                {
                    if (err == NativeMethods.ERROR_MORE_DATA)
                    {
                        startLcn = nextLcn;
                        if (startLcn * (long)bytesPerCluster >= TotalSize) break;
                        continue;
                    }
                    break;
                }
            }

            return true;
        }
    }

    internal class RawDeviceWriter : IDisposable
    {
        private FileStream? _stream;

        public RawDeviceWriter(string devicePath)
        {
            var handle = NativeMethods.CreateFile(devicePath,
                NativeMethods.GENERIC_WRITE,
                NativeMethods.FILE_SHARE_READ | NativeMethods.FILE_SHARE_WRITE,
                IntPtr.Zero,
                NativeMethods.OPEN_EXISTING,
                NativeMethods.FILE_ATTRIBUTE_NORMAL,
                IntPtr.Zero);

            if (handle.IsInvalid)
                throw new IOException($"Failed to open device for write {devicePath}");

            _stream = new FileStream(handle, FileAccess.Write);
        }

        public void Dispose()
        {
            _stream?.Dispose();
            _stream = null;
        }

        public void Seek(long offset)
        {
            _stream!.Seek(offset, SeekOrigin.Begin);
        }

        public void Write(byte[] buffer, int offset, int count)
        {
            _stream!.Write(buffer, offset, count);
        }

        public void Flush()
        {
            _stream!.Flush(true);
        }
    }

    internal static class NativeMethods
    {
        public const uint GENERIC_READ = 0x80000000;
    public const uint GENERIC_WRITE = 0x40000000;
        public const uint FILE_SHARE_READ = 0x00000001;
        public const uint FILE_SHARE_WRITE = 0x00000002;
    public const uint OPEN_EXISTING = 3;
    public const uint FILE_ATTRIBUTE_NORMAL = 0x80;
    public const uint FILE_FLAG_OVERLAPPED = 0x40000000;
    public const uint FILE_FLAG_SEQUENTIAL_SCAN = 0x08000000;
    public const uint FILE_SHARE_DELETE = 0x00000004;

        public const uint IOCTL_DISK_GET_DRIVE_GEOMETRY_EX = 0x000700A0;
        public const uint IOCTL_DISK_GET_LENGTH_INFO = 0x0007405C;
        public const uint FSCTL_GET_NTFS_VOLUME_DATA = 0x00090064;
        public const uint FSCTL_GET_VOLUME_BITMAP = 0x0009006F;
        public const int ERROR_MORE_DATA = 234;

        [StructLayout(LayoutKind.Sequential)]
        public struct DISK_GEOMETRY
        {
            public long Cylinders;
            public int MediaType;
            public int TracksPerCylinder;
            public int SectorsPerTrack;
            public int BytesPerSector;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct DISK_GEOMETRY_EX
        {
            public DISK_GEOMETRY Geometry;
            public ulong DiskSize;
            // variable-sized data follows; we don't need to marshal it here
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct GET_LENGTH_INFORMATION
        {
            public long Length;
        }

        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Auto)]
        public static extern SafeFileHandle CreateFile(string lpFileName, uint dwDesiredAccess, uint dwShareMode,
            IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

        [DllImport("kernel32", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DeviceIoControl(SafeFileHandle hDevice, uint dwIoControlCode,
            IntPtr lpInBuffer, int nInBufferSize,
            ref DISK_GEOMETRY_EX lpOutBuffer, int nOutBufferSize,
            out int lpBytesReturned, IntPtr lpOverlapped);

        [DllImport("kernel32", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DeviceIoControl(SafeFileHandle hDevice, uint dwIoControlCode,
            IntPtr lpInBuffer, int nInBufferSize,
            ref GET_LENGTH_INFORMATION lpOutBuffer, int nOutBufferSize,
            out int lpBytesReturned, IntPtr lpOverlapped);

        [StructLayout(LayoutKind.Sequential)]
        public struct NTFS_VOLUME_DATA_BUFFER
        {
            public long VolumeSerialNumber;
            public long NumberSectors;
            public long TotalClusters;
            public long FreeClusters;
            public long TotalReserved;
            public uint BytesPerSector;
            public uint BytesPerCluster;
            public uint BytesPerFileRecordSegment;
            public uint ClustersPerFileRecordSegment;
            public long MftValidDataLength;
            public long MftStartLcn;
            public long Mft2StartLcn;
            public long MftZoneStart;
            public long MftZoneEnd;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct STARTING_LCN_INPUT_BUFFER
        {
            public long StartingLcn;
        }

        [DllImport("kernel32", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DeviceIoControl(SafeFileHandle hDevice, uint dwIoControlCode,
            IntPtr lpInBuffer, int nInBufferSize,
            out NTFS_VOLUME_DATA_BUFFER lpOutBuffer, int nOutBufferSize,
            out int lpBytesReturned, IntPtr lpOverlapped);

        [DllImport("kernel32", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DeviceIoControl(SafeFileHandle hDevice, uint dwIoControlCode,
            ref STARTING_LCN_INPUT_BUFFER lpInBuffer, int nInBufferSize,
            byte[] lpOutBuffer, int nOutBufferSize,
            out int lpBytesReturned, IntPtr lpOverlapped);
    }
}
