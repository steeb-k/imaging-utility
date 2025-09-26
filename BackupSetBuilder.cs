using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace ImagingUtility
{
    internal static class BackupSetBuilder
    {
    public static async Task BuildBackupSetAsync(int diskNumber, string outDir, bool useVss, int? parallel = null, Func<int>? getDesired = null)
        {
            Directory.CreateDirectory(outDir);

            var layout = DiskLayoutUtils.GetDiskLayout(diskNumber);
            var manifest = new BackupSetManifest
            {
                DiskNumber = diskNumber,
                DiskSize = layout.DiskSize,
                Notes = useVss ? "Built with VSS snapshot set for mounted volumes" : "Built without VSS"
            };

            // 1) Dump partition table header (first 1 MiB)
            string ptDump = Path.Combine(outDir, $"disk{diskNumber}-pt.bin");
            await DumpFirstBytesAsync($"\\\\.\\PhysicalDrive{diskNumber}", ptDump, 1048576);
            manifest.PartitionTableDump = Path.GetFileName(ptDump);

            // 2) Separate partitions into VSS-capable and raw-needed
            var vssVolumes = layout.Partitions
                .Where(p => IsVssCapableFs(p.FileSystem) && !string.IsNullOrEmpty(p.DriveLetter))
                .ToList();
            var rawParts = layout.Partitions
                .Where(p => !vssVolumes.Contains(p))
                .ToList();

            // 3) Snapshot set across all VSS-capable volumes
            var snaps = new List<VssSetUtils.SnapshotInfo>();
            if (useVss && vssVolumes.Any())
            {
                var set = new VssSetUtils();
                var vols = vssVolumes.Select(p => p.DriveLetter!.EndsWith("\\") ? p.DriveLetter! : p.DriveLetter + "\\");
                try
                {
                    snaps = set.CreateSnapshotSet(vols);
                }
                catch (Exception)
                {
                    // Fallback: per-volume snapshots via WMI
                    snaps.Clear();
                    foreach (var p in vssVolumes)
                    {
                        var vss = new VssUtils();
                        var info = vss.CreateSnapshot(p.DriveLetter!.EndsWith("\\") ? p.DriveLetter! : p.DriveLetter + "\\");
                        snaps.Add(new VssSetUtils.SnapshotInfo(p.DriveLetter!, info.ShadowId, info.DeviceObject ?? p.DriveLetter!));
                    }
                }
            }

            try
            {
                // 4) Image VSS-capable volumes from snapshot devices
                foreach (var p in vssVolumes)
                {
                    string vol = p.DriveLetter!;
                    var snap = snaps.FirstOrDefault(s => s.Volume.StartsWith(vol, StringComparison.OrdinalIgnoreCase));
                    string device = snap?.DeviceObject ?? ($"\\\\.\\{vol.TrimEnd(':')}:");
                    string imgName = $"part{p.Index:D2}-{Sanitize(vol)}.skzimg";
                    string outPath = Path.Combine(outDir, imgName);

                    using var reader = new RawDeviceReader(device);
                    using var outFs = new FileStream(outPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
                    var writer = new ChunkedZstdWriter(outFs, reader.SectorSize, 64 * 1024 * 1024, deviceLength: reader.TotalSize);
                    using (var prog = new ConsoleProgressScope($"Imaging {vol}"))
                    {
                        // Default to used-only for NTFS sets (faster, smaller); fall back to full if bitmap not available
                        if (reader.TryGetNtfsBytesPerCluster(out _))
                            writer.WriteAllocatedOnly(reader, (done, total) => prog.Report(done, total), parallel, getDesired);
                        else
                            await writer.WriteFromAsync(reader, 0, p.Size, (done, total) => prog.Report(done, total), parallel, getDesired); // cap to partition size
                    }

                    manifest.Partitions.Add(new PartitionEntry
                    {
                        Index = p.Index,
                        StartingOffset = p.StartingOffset,
                        Size = p.Size,
                        Type = p.Type,
                        DriveLetter = p.DriveLetter,
                        VolumeLabel = p.VolumeLabel,
                        FileSystem = p.FileSystem,
                        ImageFile = Path.GetFileName(outPath)
                    });
                }

                // 5) Raw-dump the remaining partitions (EFI/MSR/Recovery or unknown FS)
                foreach (var p in rawParts)
                {
                    string dumpName = $"part{p.Index:D2}-raw.bin";
                    string outPath = Path.Combine(outDir, dumpName);
                    using (var prog = new ConsoleProgressScope($"Dumping raw part {p.Index}"))
                    {
                        await DumpPartitionRawAsync(diskNumber, p.StartingOffset, p.Size, outPath, (done, total) => prog.Report(done, total));
                    }

                    manifest.Partitions.Add(new PartitionEntry
                    {
                        Index = p.Index,
                        StartingOffset = p.StartingOffset,
                        Size = p.Size,
                        Type = p.Type,
                        DriveLetter = p.DriveLetter,
                        VolumeLabel = p.VolumeLabel,
                        FileSystem = p.FileSystem,
                        RawDump = Path.GetFileName(outPath)
                    });
                }
            }
            finally
            {
                // Cleanup snapshots by ID
                try
                {
                    var ids = snaps.Select(s => s.SnapshotId).Where(id => !string.IsNullOrEmpty(id)).ToList();
                    if (ids.Count > 0)
                    {
                        var set = new VssSetUtils();
                        set.DeleteSnapshots(ids);
                    }
                }
                catch { }
            }

            // 6) Save manifest
            BackupSetIO.Save(Path.Combine(outDir, "backup.manifest.json"), manifest);
        }

        public static void RestoreSetToRaw(string setDir, string outRawPath)
        {
            var manifest = BackupSetIO.Load(Path.Combine(setDir, "backup.manifest.json"));
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outRawPath))!);

            using var outFs = new FileStream(outRawPath, FileMode.Create, FileAccess.ReadWrite, FileShare.Read);
            outFs.SetLength(manifest.DiskSize);

            // Restore partition table/header if available
            if (!string.IsNullOrEmpty(manifest.PartitionTableDump))
            {
                var ptPath = Path.Combine(setDir, manifest.PartitionTableDump);
                if (File.Exists(ptPath))
                {
                    using var inFs = new FileStream(ptPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                    long toCopy = Math.Min(inFs.Length, manifest.DiskSize);
                    inFs.CopyTo(outFs);
                    // Ensure we're not beyond file start for next writes
                    outFs.Seek(0, SeekOrigin.Begin);
                }
            }

            long total = 0;
            foreach (var p in manifest.Partitions) total += p.Size;
            long written = 0;
            using (var prog = new ConsoleProgressScope("Restoring to raw"))
            {
                // Rehydrate each partition
                foreach (var p in manifest.Partitions.OrderBy(p => p.StartingOffset))
                {
                    if (!string.IsNullOrEmpty(p.ImageFile))
                    {
                        // Reconstruct from compressed image
                        var imgPath = Path.Combine(setDir, p.ImageFile);
                        using var reader = new ImageReader(imgPath);
                        using var imgFs = new FileStream(imgPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                        using var dec = new ZstdSharp.Decompressor();
                        foreach (var e in reader.Index)
                        {
                            imgFs.Seek(e.FileOffset, SeekOrigin.Begin);
                            var compressed = new byte[e.CompressedLength];
                            int read = imgFs.Read(compressed, 0, compressed.Length);
                            if (read != compressed.Length) throw new IOException("Unexpected EOF");
                            var decompressed = dec.Unwrap(compressed).ToArray();
                            outFs.Seek(p.StartingOffset + e.DeviceOffset, SeekOrigin.Begin);
                            outFs.Write(decompressed, 0, decompressed.Length);
                            written += decompressed.Length;
                            prog.Report(written, total);
                        }
                    }
                    else if (!string.IsNullOrEmpty(p.RawDump))
                    {
                        // Copy raw dump back to its offset
                        var dump = Path.Combine(setDir, p.RawDump);
                        using var inFs = new FileStream(dump, FileMode.Open, FileAccess.Read, FileShare.Read);
                        outFs.Seek(p.StartingOffset, SeekOrigin.Begin);
                        byte[] buffer = new byte[4 * 1024 * 1024];
                        int read;
                        while ((read = inFs.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            outFs.Write(buffer, 0, read);
                            written += read;
                            prog.Report(written, total);
                        }
                    }
                }
            }
            outFs.Flush();
        }

        public static void RestoreSetToPhysical(string setDir, int targetDiskNumber)
        {
            var manifest = BackupSetIO.Load(Path.Combine(setDir, "backup.manifest.json"));
            string device = $"\\\\.\\PhysicalDrive{targetDiskNumber}";

            // WARNING: destructive operation
            Console.WriteLine($"[!] Restoring to physical disk {device}. This will overwrite data.");

            using var writer = new RawDeviceWriter(device);

            // 1) Write partition table/header if present
            if (!string.IsNullOrEmpty(manifest.PartitionTableDump))
            {
                var ptPath = Path.Combine(setDir, manifest.PartitionTableDump);
                if (File.Exists(ptPath))
                {
                    using var inFs = new FileStream(ptPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                    byte[] buffer = new byte[4 * 1024 * 1024];
                    int read;
                    writer.Seek(0);
                    while ((read = inFs.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        writer.Write(buffer, 0, read);
                    }
                    writer.Flush();
                }
            }

            // 2) Replay partitions with progress
            long total = 0; foreach (var p in manifest.Partitions) total += p.Size;
            long written = 0;
            using (var prog = new ConsoleProgressScope($"Restore to {device}"))
            {
                foreach (var p in manifest.Partitions.OrderBy(p => p.StartingOffset))
                {
                    if (!string.IsNullOrEmpty(p.ImageFile))
                    {
                        var imgPath = Path.Combine(setDir, p.ImageFile);
                        using var reader = new ImageReader(imgPath);
                        using var imgFs = new FileStream(imgPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                        using var dec = new ZstdSharp.Decompressor();
                        foreach (var e in reader.Index)
                        {
                            imgFs.Seek(e.FileOffset, SeekOrigin.Begin);
                            var compressed = new byte[e.CompressedLength];
                            int read = imgFs.Read(compressed, 0, compressed.Length);
                            if (read != compressed.Length) throw new IOException("Unexpected EOF");
                            var decompressed = dec.Unwrap(compressed).ToArray();
                            writer.Seek(p.StartingOffset + e.DeviceOffset);
                            writer.Write(decompressed, 0, decompressed.Length);
                            written += decompressed.Length;
                            prog.Report(written, total);
                        }
                        writer.Flush();
                    }
                    else if (!string.IsNullOrEmpty(p.RawDump))
                    {
                        var dump = Path.Combine(setDir, p.RawDump);
                        using var inFs = new FileStream(dump, FileMode.Open, FileAccess.Read, FileShare.Read);
                        writer.Seek(p.StartingOffset);
                        byte[] buffer = new byte[4 * 1024 * 1024];
                        int read;
                        while ((read = inFs.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            writer.Write(buffer, 0, read);
                            written += read;
                            prog.Report(written, total);
                        }
                        writer.Flush();
                    }
                }
            }
        }

        private static bool IsVssCapableFs(string? fs)
        {
            if (string.IsNullOrEmpty(fs)) return false;
            // VSS supports NTFS; newer Windows support ReFS in some scenarios. Be conservative: NTFS only here.
            return fs.Equals("NTFS", StringComparison.OrdinalIgnoreCase);
        }

        private static async Task DumpFirstBytesAsync(string device, string outPath, long bytes)
        {
            using var reader = new RawDeviceReader(device);
            using var outFs = new FileStream(outPath, FileMode.Create, FileAccess.Write, FileShare.Read);
            byte[] buffer = new byte[4 * 1024 * 1024];
            long remaining = Math.Min(bytes, reader.TotalSize);
            while (remaining > 0)
            {
                int toRead = (int)Math.Min(buffer.Length, remaining);
                int read = reader.ReadAligned(buffer, 0, toRead);
                if (read <= 0) break;
                await outFs.WriteAsync(buffer, 0, read);
                remaining -= read;
            }
        }

        private static async Task DumpPartitionRawAsync(int diskNumber, long startOffset, long size, string outPath, Action<long,long>? progress = null)
        {
            using var reader = new RawDeviceReader($"\\\\.\\PhysicalDrive{diskNumber}");
            using var outFs = new FileStream(outPath, FileMode.Create, FileAccess.Write, FileShare.Read);
            reader.Seek(startOffset);
            long remaining = size;
            byte[] buffer = new byte[4 * 1024 * 1024];
            long written = 0;
            while (remaining > 0)
            {
                int toRead = (int)Math.Min(buffer.Length, remaining);
                int read = reader.ReadAligned(buffer, 0, toRead);
                if (read <= 0) break;
                await outFs.WriteAsync(buffer, 0, read);
                remaining -= read;
                written += read;
                progress?.Invoke(written, size);
            }
        }

        private static string Sanitize(string s)
        {
            var allowed = new List<char>();
            foreach (var ch in s)
                if (char.IsLetterOrDigit(ch)) allowed.Add(ch);
            return new string(allowed.ToArray());
        }
    }
}
