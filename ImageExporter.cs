using System;
using System.IO;
using System.Text;
using ZstdSharp;

namespace ImagingUtility
{
    internal static class ImageExporter
    {
        // Export to raw disk image by replaying chunks (decompressing and writing at offsets)
        public static void ExportToRaw(string imagePath, string outRawPath, Action<string>? log = null)
        {
            using var reader = new ImageReader(imagePath);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outRawPath))!);
            using var outFs = new FileStream(outRawPath, FileMode.Create, FileAccess.ReadWrite, FileShare.Read);
            outFs.SetLength(reader.DeviceLength);
            using var dec = new Decompressor();
            using var imgFs = new FileStream(imagePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            long written = 0;
            for (int i = 0; i < reader.Index.Count; i++)
            {
                var e = reader.Index[i];
                imgFs.Seek(e.FileOffset, SeekOrigin.Begin);
                var compressed = new byte[e.CompressedLength];
                int read = imgFs.Read(compressed, 0, compressed.Length);
                if (read != compressed.Length)
                    throw new IOException($"Unexpected EOF reading chunk {i}");
                var decompressed = dec.Unwrap(compressed).ToArray();
                if (decompressed.Length != e.UncompressedLength)
                    throw new InvalidDataException($"Chunk {i} length mismatch");

                outFs.Seek(e.DeviceOffset, SeekOrigin.Begin);
                outFs.Write(decompressed, 0, decompressed.Length);
                written += decompressed.Length;
                if (i % 128 == 0) log?.Invoke($"Exported {i}/{reader.Index.Count} chunks ({written} bytes)...");
            }
            log?.Invoke($"Raw export complete: {outRawPath}");
        }

        public static void ExportToVhdFixed(string imagePath, string outVhdPath, Action<string>? log = null)
        {
            using var reader = new ImageReader(imagePath);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outVhdPath))!);
            using var outFs = new FileStream(outVhdPath, FileMode.Create, FileAccess.ReadWrite, FileShare.Read);
            // Write raw payload first
            outFs.SetLength(reader.DeviceLength);
            using var dec = new Decompressor();
            using var imgFs = new FileStream(imagePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            long written = 0;
            for (int i = 0; i < reader.Index.Count; i++)
            {
                var e = reader.Index[i];
                imgFs.Seek(e.FileOffset, SeekOrigin.Begin);
                var compressed = new byte[e.CompressedLength];
                int read = imgFs.Read(compressed, 0, compressed.Length);
                if (read != compressed.Length)
                    throw new IOException($"Unexpected EOF reading chunk {i}");
                var decompressed = dec.Unwrap(compressed).ToArray();
                if (decompressed.Length != e.UncompressedLength)
                    throw new InvalidDataException($"Chunk {i} length mismatch");

                outFs.Seek(e.DeviceOffset, SeekOrigin.Begin);
                outFs.Write(decompressed, 0, decompressed.Length);
                written += decompressed.Length;
                if (i % 128 == 0) log?.Invoke($"Exported {i}/{reader.Index.Count} chunks ({written} bytes)...");
            }

            // Append VHD footer
            var footer = BuildVhdFixedFooter(reader.DeviceLength);
            outFs.Seek(0, SeekOrigin.End);
            outFs.Write(footer, 0, footer.Length);
            outFs.Flush();
            log?.Invoke($"VHD export complete: {outVhdPath}");
        }

        private static byte[] BuildVhdFixedFooter(long diskSize)
        {
            byte[] f = new byte[512];
            void PutAscii(int offset, string s)
            {
                var b = Encoding.ASCII.GetBytes(s);
                Array.Copy(b, 0, f, offset, Math.Min(b.Length, 8));
            }
            void PutUInt32BE(int offset, uint v)
            {
                f[offset + 0] = (byte)((v >> 24) & 0xFF);
                f[offset + 1] = (byte)((v >> 16) & 0xFF);
                f[offset + 2] = (byte)((v >> 8) & 0xFF);
                f[offset + 3] = (byte)(v & 0xFF);
            }
            void PutUInt64BE(int offset, ulong v)
            {
                f[offset + 0] = (byte)((v >> 56) & 0xFF);
                f[offset + 1] = (byte)((v >> 48) & 0xFF);
                f[offset + 2] = (byte)((v >> 40) & 0xFF);
                f[offset + 3] = (byte)((v >> 32) & 0xFF);
                f[offset + 4] = (byte)((v >> 24) & 0xFF);
                f[offset + 5] = (byte)((v >> 16) & 0xFF);
                f[offset + 6] = (byte)((v >> 8) & 0xFF);
                f[offset + 7] = (byte)(v & 0xFF);
            }

            // Cookie
            PutAscii(0, "conectix");
            // Features: 0x00000002
            PutUInt32BE(8, 0x00000002);
            // FileFormatVersion: 0x00010000
            PutUInt32BE(12, 0x00010000);
            // DataOffset: for fixed = 0xFFFFFFFFFFFFFFFF
            PutUInt64BE(16, 0xFFFFFFFFFFFFFFFF);
            // TimeStamp: seconds since 2000-01-01
            var secs = (uint)(DateTimeOffset.UtcNow - new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero)).TotalSeconds;
            PutUInt32BE(24, secs);
            // Creator Application 'IUTL'
            f[28] = (byte)'I'; f[29] = (byte)'U'; f[30] = (byte)'T'; f[31] = (byte)'L';
            // Creator Version 0x00010000
            PutUInt32BE(32, 0x00010000);
            // Creator Host OS 'Wi2k'
            f[36] = (byte)'W'; f[37] = (byte)'i'; f[38] = (byte)'2'; f[39] = (byte)'k';
            // OriginalSize & CurrentSize
            PutUInt64BE(40, (ulong)diskSize);
            PutUInt64BE(48, (ulong)diskSize);
            // Disk Geometry
            GetVhdChs(diskSize, out ushort cyl, out byte heads, out byte spt);
            f[56] = (byte)((cyl >> 8) & 0xFF);
            f[57] = (byte)(cyl & 0xFF);
            f[58] = heads;
            f[59] = spt;
            // Disk Type: 2 (fixed)
            PutUInt32BE(60, 2);
            // Checksum will be filled later at 64..67
            // UniqueId
            var guid = Guid.NewGuid().ToByteArray();
            Array.Copy(guid, 0, f, 68, 16);
            // SavedState = 0 (byte at 84)
            f[84] = 0;

            // Compute checksum (sum of all bytes with checksum zeroed)
            f[64] = f[65] = f[66] = f[67] = 0;
            uint sum = 0;
            for (int i = 0; i < 512; i++) sum += f[i];
            uint checksum = ~sum;
            PutUInt32BE(64, checksum);
            return f;
        }

        private static void GetVhdChs(long bytes, out ushort cylinders, out byte heads, out byte sectorsPerTrack)
        {
            const int sectorSize = 512;
            ulong totalSectors = (ulong)(bytes / sectorSize);
            // Use common LBA mapping geometry: heads=255, spt=63
            heads = 255;
            sectorsPerTrack = 63;
            ulong cyl = totalSectors / ((ulong)heads * sectorsPerTrack);
            if (cyl > 65535) cyl = 65535;
            cylinders = (ushort)cyl;
        }
    }
}
