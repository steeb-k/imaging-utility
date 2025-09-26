using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Security.Principal;
using System.Threading.Tasks;
using System.Text;
using System.Collections.Generic;
using System.Reflection;

namespace ImagingUtility
{
    internal class Program
    {
        static async Task<int> Main(string[] args)
        {
            if (!IsElevated())
            {
                Console.Error.WriteLine("This tool must be run as Administrator.");
                return 2;
            }

            if (args.Length == 0)
            {
                PrintHelp();
                return 1;
            }

            var cmd = args[0].ToLowerInvariant();
            try
            {
                if (cmd == "list")
                {
                    DriveLister.ListPhysicalDrives();
                    return 0;
                }
                else if (cmd == "image")
                {
                    // image --device \\.\\PhysicalDriveN|C: --out <file> [--use-vss] [--resume]
                    // or     --volumes C:,D: --out-dir <dir> [--use-vss] [--resume]
                    var deviceArg = GetArgValue(args, "--device");
                    var outArg = GetArgValue(args, "--out");
                    var useVss = Array.Exists(args, a => a.Equals("--use-vss", StringComparison.OrdinalIgnoreCase));
                    var resume = Array.Exists(args, a => a.Equals("--resume", StringComparison.OrdinalIgnoreCase));
                    // Default to used-only; allow opt-out via --all-blocks or --no-used-only
                    bool usedOnly = true;
                    if (Array.Exists(args, a => a.Equals("--all-blocks", StringComparison.OrdinalIgnoreCase) || a.Equals("--no-used-only", StringComparison.OrdinalIgnoreCase)))
                        usedOnly = false;
                    else if (Array.Exists(args, a => a.Equals("--used-only", StringComparison.OrdinalIgnoreCase)))
                        usedOnly = true;
                    var volumesArg = GetArgValue(args, "--volumes");
                    var outDirArg = GetArgValue(args, "--out-dir");
                    var maxBytesArg = GetArgValue(args, "--max-bytes");
                    var chunkSizeArg = GetArgValue(args, "--chunk-size");
                    long? maxBytes = null;
                    if (!string.IsNullOrEmpty(maxBytesArg) && long.TryParse(maxBytesArg, out var parsed))
                        maxBytes = parsed;
                    int? chunkSize = null;
                    int? parallel = null;
                    var parArg = GetArgValue(args, "--parallel");
                    if (!string.IsNullOrEmpty(parArg) && int.TryParse(parArg, out var pval) && pval > 0)
                        parallel = pval;
                    var parCtlFile = GetArgValue(args, "--parallel-control-file");
                    var parCtlPipe = GetArgValue(args, "--parallel-control-pipe");
                    if (!string.IsNullOrEmpty(chunkSizeArg))
                    {
                        if (TryParseSize(chunkSizeArg, out long cs) && cs > 0 && cs <= int.MaxValue)
                            chunkSize = (int)cs;
                        else if (int.TryParse(chunkSizeArg, out var ci) && ci > 0)
                            chunkSize = ci;
                    }

                            var parCtlFile = GetArgValue(args, "--parallel-control-file");
                            int pipelineDepth = 2; // Default value for pipeline depth
                            var pipeDepthArg = GetArgValue(args, "--pipeline-depth");
                            if (!string.IsNullOrEmpty(pipeDepthArg) && int.TryParse(pipeDepthArg, out var pd) && pd >= 1 && pd <= 8)
                                pipelineDepth = pd;
                    if (!string.IsNullOrEmpty(volumesArg) && !string.IsNullOrEmpty(outDirArg))
                    {
                        var getDesiredMv = BuildParallelProvider(parallel, parCtlFile, parCtlPipe);
                        return await ImageMultipleVolumes(volumesArg, outDirArg, useVss, resume, maxBytes, usedOnly, chunkSize, parallel, getDesiredMv);
                    }

                    if (string.IsNullOrEmpty(deviceArg) || string.IsNullOrEmpty(outArg))
                    {
                        Console.Error.WriteLine("Usage: image --device \\ \\ \\ . \\ \\ PhysicalDriveN|C: --out <output-file> [--use-vss] [--resume]".Replace(" ", string.Empty));
                        Console.Error.WriteLine("   or: image --volumes C:,D: --out-dir <directory> [--use-vss] [--resume]");
                        return 1;
                    }

                    string? snapshotId = null;
                    string deviceToRead = deviceArg;
                    try
                    {
                        if (useVss)
                        {
                            // deviceArg expected to be a volume like C:\\ or C:
                            var vol = NormalizeVolume(deviceArg);
                            Console.WriteLine($"Creating VSS snapshot for {vol} ...");
                            var vss = new VssUtils();
                            var info2 = vss.CreateSnapshot(vol);
                            snapshotId = info2.ShadowId;
                            deviceToRead = info2.DeviceObject ?? deviceArg;
                            Console.WriteLine($"Snapshot created: {snapshotId}, device: {deviceToRead}");
                        }

                        Console.WriteLine($"Imaging device {deviceToRead} to {outArg} ...");
                        var overallSw = System.Diagnostics.Stopwatch.StartNew();
                        using var reader = new RawDeviceReader(deviceToRead);
                        // If resume and output exists with a valid footer, resume from last chunk
                        var append = resume && File.Exists(outArg);
                        List<IndexEntry>? existingIndex = null;
                        long startOffset = 0;
                        FileMode mode = append ? FileMode.Open : FileMode.Create;
                        using var outStream = new FileStream(outArg, mode, FileAccess.ReadWrite, FileShare.None);
                        if (append)
                        {
                            try
                            {
                                var readerImg = new ImageReader(outArg);
                                existingIndex = readerImg.Index;
                                var resumePoint = readerImg.ComputeResumePoint();
                                startOffset = resumePoint.nextDeviceOffset;
                                outStream.Seek(0, SeekOrigin.End); // we'll overwrite footer when writing new chunks
                                // truncate footer to append more chunks
                                outStream.SetLength(readerImg.IndexStart); // cut off old index+tail
                            }
                            catch
                            {
                                Console.WriteLine("Resume requested but existing image is invalid; starting fresh.");
                                outStream.SetLength(0);
                            }
                        }

                        var writer = new ChunkedZstdWriter(outStream, reader.SectorSize, chunkSize ?? (64 * 1024 * 1024), append: append, existingIndex: existingIndex, deviceLength: reader.TotalSize);
                        Func<int>? getDesired = BuildParallelProvider(parallel, parCtlFile, parCtlPipe);
                        using (var p = new ConsoleProgressScope($"Imaging {deviceToRead}"))
                        {
                            bool canUsedOnly = usedOnly && reader.TryGetNtfsBytesPerCluster(out _);
                            if (canUsedOnly && startOffset == 0)
                            {
                                writer.WriteAllocatedOnly(reader, (done, total) => p.Report(done, total), parallel, getDesired);
                            }
                            else
                            {
                                if (canUsedOnly && startOffset > 0)
                                    Console.WriteLine("[info] Resume with --used-only not yet optimized; falling back to full-range resume for correctness.");
                                await writer.WriteFromAsync(reader, startOffset, maxBytes, (done, total) => p.Report(done, total), parallel, getDesired);
                                        writer.WriteAllocatedOnly(reader, (done, total) => p.Report(done, total), parallel, getDesired, pipelineDepth);
                        }

                        overallSw.Stop();
                        double secs = Math.Max(0.001, overallSw.Elapsed.TotalSeconds);
                        long totalRaw = 0;
                                        var res = await writer.WriteFromAsync(reader, startOffset, maxBytes, (done, total) => p.Report(done, total), parallel, getDesired, pipelineDepth);
                        {
                            using var imgReader = new ImageReader(outArg);
                            foreach (var e in imgReader.Index) totalRaw += e.UncompressedLength;
                        }
                        catch { }
                        if (totalRaw <= 0) totalRaw = reader.TotalSize; // fallback
                        Console.WriteLine($"Image complete. Elapsed: {ConsoleProgressScope.FormatTime(overallSw.Elapsed)}  Avg: {ConsoleProgressScope.FormatBytes((long)(totalRaw / secs))}/s");
                    }
                    finally
                    {
                        if (!string.IsNullOrEmpty(snapshotId))
                        {
                            try
                            {
                                // Delete snapshot via WMI
                                try { new VssUtils().DeleteSnapshot(snapshotId); } catch { }
                                Console.WriteLine($"Deleted snapshot {snapshotId}");
                            }
                            catch (Exception ex)
                            {
                                Console.Error.WriteLine($"Failed to delete snapshot {snapshotId}: {ex.Message}");
                            }
                        }
                    }
                    return 0;
                }
                else if (cmd == "verify")
                {
                    var inArg = GetArgValue(args, "--in");
                    if (string.IsNullOrEmpty(inArg) || !File.Exists(inArg))
                    {
                        Console.Error.WriteLine("Usage: verify --in <image-file>");
                        return 1;
                    }
                    var r = new ImageReader(inArg);
                    int? parallel = null;
                    var parArg = GetArgValue(args, "--parallel");
                    if (!string.IsNullOrEmpty(parArg) && int.TryParse(parArg, out var pval) && pval > 0)
                        parallel = pval;
                    bool quick = Array.Exists(args, a => a.Equals("--quick", StringComparison.OrdinalIgnoreCase));
                    using (var p = new ConsoleProgressScope($"Verifying {Path.GetFileName(inArg)}"))
                    {
                        var sw = System.Diagnostics.Stopwatch.StartNew();
                        bool ok = quick ? VerifyQuick(r, msg => Console.WriteLine(msg), (done, total) => p.Report(done, total), parallel)
                                         : r.VerifyAll(msg => Console.WriteLine(msg), (done, total) => p.Report(done, total), parallel);
                        sw.Stop();
                        double secs = Math.Max(0.001, sw.Elapsed.TotalSeconds);
                        long totalBytes = 0; foreach (var e in r.Index) totalBytes += e.CompressedLength;
                        var avg = ConsoleProgressScope.FormatBytes((long)(totalBytes / secs));
                        p.Complete();
                        Console.WriteLine(ok ? $"Verify: OK. Elapsed: {ConsoleProgressScope.FormatTime(sw.Elapsed)}  Avg: {avg}/s" : $"Verify: FAILED. Elapsed: {ConsoleProgressScope.FormatTime(sw.Elapsed)}  Avg: {avg}/s");
                        return ok ? 0 : 4;
                    }
                    
                }
                else if (cmd == "export-raw")
                {
                    var inArg = GetArgValue(args, "--in");
                    var outArg = GetArgValue(args, "--out");
                    if (string.IsNullOrEmpty(inArg) || string.IsNullOrEmpty(outArg) || !File.Exists(inArg))
                    {
                        Console.Error.WriteLine("Usage: export-raw --in <image-file> --out <raw-file>");
                        return 1;
                    }
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    ImageExporter.ExportToRaw(inArg, outArg, msg => Console.WriteLine(msg));
                    sw.Stop();
                    double secs = Math.Max(0.001, sw.Elapsed.TotalSeconds);
                    var rImg = new ImageReader(inArg);
                    long totalBytes = 0; foreach (var e in rImg.Index) totalBytes += e.UncompressedLength;
                    var avg = ConsoleProgressScope.FormatBytes((long)(totalBytes / secs));
                    Console.WriteLine($"Export to raw complete. Elapsed: {ConsoleProgressScope.FormatTime(sw.Elapsed)}  Avg: {avg}/s");
                    return 0;
                }
                else if (cmd == "export-vhd")
                {
                    var inArg = GetArgValue(args, "--in");
                    var outArg = GetArgValue(args, "--out");
                    if (string.IsNullOrEmpty(inArg) || string.IsNullOrEmpty(outArg) || !File.Exists(inArg))
                    {
                        Console.Error.WriteLine("Usage: export-vhd --in <image-file> --out <vhd-file>");
                        return 1;
                    }
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    ImageExporter.ExportToVhdFixed(inArg, outArg, msg => Console.WriteLine(msg));
                    sw.Stop();
                    double secs = Math.Max(0.001, sw.Elapsed.TotalSeconds);
                    var rImg2 = new ImageReader(inArg);
                    long totalBytes2 = 0; foreach (var e in rImg2.Index) totalBytes2 += e.UncompressedLength;
                    var avg2 = ConsoleProgressScope.FormatBytes((long)(totalBytes2 / secs));
                    Console.WriteLine($"Export to VHD complete. Elapsed: {ConsoleProgressScope.FormatTime(sw.Elapsed)}  Avg: {avg2}/s");
                    return 0;
                }
                else if (cmd == "backup-disk")
                {
                    // backup-disk --disk N --out-dir <dir> [--use-vss]
                    var diskArg = GetArgValue(args, "--disk");
                    var outDirArg = GetArgValue(args, "--out-dir");
                    var useVss = Array.Exists(args, a => a.Equals("--use-vss", StringComparison.OrdinalIgnoreCase));
                    int? parallel = null;
                    var parArg = GetArgValue(args, "--parallel");
                    if (!string.IsNullOrEmpty(parArg) && int.TryParse(parArg, out var pval) && pval > 0)
                        parallel = pval;
                    var parCtlFileB = GetArgValue(args, "--parallel-control-file");
                    var parCtlPipeB = GetArgValue(args, "--parallel-control-pipe");
                    if (string.IsNullOrEmpty(diskArg) || string.IsNullOrEmpty(outDirArg) || !int.TryParse(diskArg, out var diskNum))
                    {
                        Console.Error.WriteLine("Usage: backup-disk --disk N --out-dir <dir> [--use-vss] [--parallel N]");
                        return 1;
                    }
                    Console.WriteLine($"Building backup set for disk {diskNum} -> {outDirArg} (useVss={useVss}) ...");
                    var swBackup = System.Diagnostics.Stopwatch.StartNew();
                    var getDesiredB = BuildParallelProvider(parallel, parCtlFileB, parCtlPipeB);
                    await BackupSetBuilder.BuildBackupSetAsync(diskNum, outDirArg, useVss, parallel, getDesiredB);
                    swBackup.Stop();
                    Console.WriteLine($"Backup set complete. Elapsed: {ConsoleProgressScope.FormatTime(swBackup.Elapsed)}");
                    return 0;
                }
                else if (cmd == "restore-set")
                {
                    // restore-set --set-dir <dir> --out-raw <file>
                    var setDir = GetArgValue(args, "--set-dir");
                    var outRaw = GetArgValue(args, "--out-raw");
                    if (string.IsNullOrEmpty(setDir) || string.IsNullOrEmpty(outRaw))
                    {
                        Console.Error.WriteLine("Usage: restore-set --set-dir <dir> --out-raw <file>");
                        return 1;
                    }
                    Console.WriteLine($"Restoring set {setDir} -> {outRaw} ...");
                    var swRestoreRaw = System.Diagnostics.Stopwatch.StartNew();
                    BackupSetBuilder.RestoreSetToRaw(setDir, outRaw);
                    swRestoreRaw.Stop();
                    double secsR = Math.Max(0.001, swRestoreRaw.Elapsed.TotalSeconds);
                    var manifestR = BackupSetIO.Load(System.IO.Path.Combine(setDir, "backup.manifest.json"));
                    long totalR = 0; foreach (var p in manifestR.Partitions) totalR += p.Size;
                    var avgR = ConsoleProgressScope.FormatBytes((long)(totalR / secsR));
                    Console.WriteLine($"Restore complete. Elapsed: {ConsoleProgressScope.FormatTime(swRestoreRaw.Elapsed)}  Avg: {avgR}/s");
                    return 0;
                }
                else if (cmd == "restore-physical")
                {
                    // restore-physical --set-dir <dir> --disk N
                    var setDir = GetArgValue(args, "--set-dir");
                    var diskArg = GetArgValue(args, "--disk");
                    if (string.IsNullOrEmpty(setDir) || string.IsNullOrEmpty(diskArg) || !int.TryParse(diskArg, out var diskNum))
                    {
                        Console.Error.WriteLine("Usage: restore-physical --set-dir <dir> --disk N");
                        return 1;
                    }
                    Console.WriteLine($"[!] WARNING: This will overwrite data on disk {diskNum}. Proceeding...");
                    var swRestorePhys = System.Diagnostics.Stopwatch.StartNew();
                    BackupSetBuilder.RestoreSetToPhysical(setDir, diskNum);
                    swRestorePhys.Stop();
                    double secsP = Math.Max(0.001, swRestorePhys.Elapsed.TotalSeconds);
                    var manifestP = BackupSetIO.Load(System.IO.Path.Combine(setDir, "backup.manifest.json"));
                    long totalP = 0; foreach (var p in manifestP.Partitions) totalP += p.Size;
                    var avgP = ConsoleProgressScope.FormatBytes((long)(totalP / secsP));
                    Console.WriteLine($"Restore to physical complete. Elapsed: {ConsoleProgressScope.FormatTime(swRestorePhys.Elapsed)}  Avg: {avgP}/s");
                    return 0;
                }
                else if (cmd == "dump-pt")
                {
                    // Dump first N bytes from a physical disk (e.g., MBR/GPT headers) for consistent system backups
                    var deviceArg = GetArgValue(args, "--device");
                    var outArg = GetArgValue(args, "--out");
                    var bytesArg = GetArgValue(args, "--bytes");
                    if (string.IsNullOrEmpty(deviceArg) || string.IsNullOrEmpty(outArg))
                    {
                        Console.Error.WriteLine("Usage: dump-pt --device \\\\.\\\\PhysicalDriveN --out <file> [--bytes N]  # default 1048576 (1 MiB)");
                        return 1;
                    }
                    long toCopy = 1048576; // 1 MiB default
                    if (!string.IsNullOrEmpty(bytesArg) && long.TryParse(bytesArg, out var n) && n > 0)
                        toCopy = n;
                    using var reader = new RawDeviceReader(deviceArg);
                    toCopy = Math.Min(toCopy, reader.TotalSize);
                    Console.WriteLine($"Dumping {toCopy} bytes from start of {deviceArg} to {outArg} ...");
                    var swDump = System.Diagnostics.Stopwatch.StartNew();
                    using var outFs = new FileStream(outArg, FileMode.Create, FileAccess.Write, FileShare.Read);
                    byte[] buffer = new byte[Math.Min(4 * 1024 * 1024, (int)Math.Min(toCopy, 4 * 1024 * 1024))];
                    long remaining = toCopy;
                    while (remaining > 0)
                    {
                        int toRead = (int)Math.Min(buffer.Length, remaining);
                        int read = reader.ReadAligned(buffer, 0, toRead);
                        if (read <= 0) break;
                        outFs.Write(buffer, 0, read);
                        remaining -= read;
                    }
                    swDump.Stop();
                    double secsD = Math.Max(0.001, swDump.Elapsed.TotalSeconds);
                    var avgD = ConsoleProgressScope.FormatBytes((long)(toCopy / secsD));
                    Console.WriteLine($"Dump complete. Elapsed: {ConsoleProgressScope.FormatTime(swDump.Elapsed)}  Avg: {avgD}/s");
                    return 0;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error: {ex.Message}\n{ex}");
                return 3;
            }

            PrintHelp();
            return 1;
        }

        static string? GetArgValue(string[] args, string name)
        {
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                    return args[i + 1];
            }
            return null;
        }

        static string NormalizeVolume(string v)
        {
            if (string.IsNullOrEmpty(v)) return v;
            // accept C: or C:\\ or full paths; ensure trailing backslash
            if (v.Length == 2 && v[1] == ':') return v + "\\";
            if (v.Length >= 2 && v[1] == ':' && (v.Length == 2 || v[2] != '\\')) return v.Substring(0, 2) + "\\";
            return v;
        }

    static void PrintHelp()
        {
            Console.WriteLine("ImagingUtility - simple prototype\n");
            Console.WriteLine("Commands:");
            Console.WriteLine("  list\t\tList physical drives");
            Console.WriteLine("  image --device \\ \\ \\ . \\ \\ PhysicalDriveN|C: --out <file> [--use-vss] [--resume] [--all-blocks] [--chunk-size N] [--max-bytes N] [--parallel N] [--parallel-control-file PATH] [--parallel-control-pipe NAME]\tCreate or resume a chunked image (default: --used-only, 64M chunk)".Replace(" ", string.Empty));
            Console.WriteLine("  image --volumes C:,D: --out-dir <dir> [--use-vss] [--resume] [--all-blocks] [--chunk-size N] [--max-bytes N] [--parallel N] [--parallel-control-file PATH] [--parallel-control-pipe NAME]\tSnapshot multiple volumes (default: --used-only, 64M chunk)");
            Console.WriteLine("  backup-disk --disk N --out-dir <dir> [--use-vss]\tCreate a full-disk backup set (manifest + per-partition images)");
            Console.WriteLine("  restore-set --set-dir <dir> --out-raw <file>\tReconstruct a raw disk image from a backup set");
            Console.WriteLine("  restore-physical --set-dir <dir> --disk N\tWrite a backup set directly to a physical disk (DESTRUCTIVE)");
            Console.WriteLine("  verify --in <file> [--parallel N] [--quick]\tVerify chunk checksums and integrity (use --quick for faster sampling-based verify)");
            Console.WriteLine("  export-raw --in <image> --out <raw>\tExport compressed image to raw disk file");
            Console.WriteLine("  export-vhd --in <image> --out <vhd>\tExport compressed image to fixed VHD (mountable in Windows)");
            Console.WriteLine("  dump-pt --device \\ \\ \\ . \\ \\ PhysicalDriveN --out <file> [--bytes N]\tDump first bytes (MBR/GPT) for system disk backups".Replace(" ", string.Empty));
        }

        private static bool TryParseSize(string input, out long bytes)
        {
            bytes = 0;
            if (string.IsNullOrWhiteSpace(input)) return false;
            input = input.Trim();
            long multiplier = 1;
            char last = char.ToUpperInvariant(input[^1]);
            if (last == 'K' || last == 'M' || last == 'G')
            {
                input = input[..^1];
                if (last == 'K') multiplier = 1024;
                else if (last == 'M') multiplier = 1024L * 1024L;
                else if (last == 'G') multiplier = 1024L * 1024L * 1024L;
            }
            if (!long.TryParse(input, out long value)) return false;
            bytes = value * multiplier;
            return true;
        }

        static bool IsElevated()
        {
#pragma warning disable CA1416
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
#pragma warning restore CA1416
        }

        private static IEnumerable<string> ParseVolumes(string volumesArg)
        {
            return volumesArg
                .Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(NormalizeVolume)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        private static string SanitizeVolumeForName(string vol)
        {
            // Expect forms like "C:\\" -> return "C"
            if (string.IsNullOrEmpty(vol)) return "vol";
            if (vol.Length >= 2 && vol[1] == ':') return char.ToUpperInvariant(vol[0]).ToString();
            // fallback: strip backslashes and invalid chars
            var s = new string(vol.Where(ch => char.IsLetterOrDigit(ch)).ToArray());
            return string.IsNullOrEmpty(s) ? "vol" : s;
        }

    private static async Task<int> ImageMultipleVolumes(string volumesArg, string outDir, bool useVss, bool resume, long? maxBytes, bool usedOnly, int? chunkSize, int? parallel, Func<int>? getDesired)
        {
            var vols = ParseVolumes(volumesArg).ToList();
            if (vols.Count == 0)
            {
                Console.Error.WriteLine("No volumes provided. Example: --volumes C:,D:");
                return 1;
            }
            Directory.CreateDirectory(outDir);

            List<(string volume, string snapshotId, string deviceObject)> snaps = new();
            bool createdSnapshots = false;
            try
            {
                if (useVss)
                {
                    Console.WriteLine($"Creating VSS snapshot set for: {string.Join(", ", vols)}");
                    try
                    {
                        var set = new VssSetUtils();
                        var infos = set.CreateSnapshotSet(vols);
                        foreach (var info in infos)
                            snaps.Add((info.Volume, info.SnapshotId, info.DeviceObject));
                        createdSnapshots = true;
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"VSS snapshot set failed: {ex.Message}");
                        Console.Error.WriteLine("Falling back to per-volume snapshots via WMI.");
                        foreach (var v in vols)
                        {
                            var vss = new VssUtils();
                            var info = vss.CreateSnapshot(v);
                            snaps.Add((v, info.ShadowId, info.DeviceObject ?? v));
                        }
                        createdSnapshots = true;
                    }
                }
                else
                {
                    // No VSS: map volumes to raw device paths like \\.\\C:
                    foreach (var v in vols)
                    {
                        string dev = $"\\\\.\\{v[0]}:"; // e.g., C:
                        snaps.Add((v, snapshotId: string.Empty, deviceObject: dev));
                    }
                }

                foreach (var s in snaps)
                {
                    string name = SanitizeVolumeForName(s.volume);
                    string outPath = Path.Combine(outDir, $"{name}.skzimg");
                    Console.WriteLine($"Imaging {s.volume} (device {s.deviceObject}) -> {outPath}");

                    using var reader = new RawDeviceReader(s.deviceObject);
                    var append = resume && File.Exists(outPath);
                    List<IndexEntry>? existingIndex = null;
                    long startOffset = 0;
                    FileMode mode = append ? FileMode.Open : FileMode.Create;
                    using var outStream = new FileStream(outPath, mode, FileAccess.ReadWrite, FileShare.None);
                    if (append)
                    {
                        try
                        {
                            var readerImg = new ImageReader(outPath);
                            existingIndex = readerImg.Index;
                            var resumePoint = readerImg.ComputeResumePoint();
                            startOffset = resumePoint.nextDeviceOffset;
                            outStream.Seek(0, SeekOrigin.End);
                            outStream.SetLength(readerImg.IndexStart);
                        }
                        catch
                        {
                            Console.WriteLine("Resume requested but existing image is invalid; starting fresh.");
                            outStream.SetLength(0);
                        }
                    }

                    var writer = new ChunkedZstdWriter(outStream, reader.SectorSize, chunkSize ?? (64 * 1024 * 1024), append: append, existingIndex: existingIndex, deviceLength: reader.TotalSize);
                    using (var p = new ConsoleProgressScope($"Imaging {s.volume}"))
                    {
                        if (usedOnly && reader.TryGetNtfsBytesPerCluster(out _))
                            writer.WriteAllocatedOnly(reader, (done, total) => p.Report(done, total), parallel, getDesired);
                        else
                            await writer.WriteFromAsync(reader, startOffset, maxBytes, (done, total) => p.Report(done, total), parallel, getDesired);
                    }
                    Console.WriteLine($"Completed {outPath}");
                }
                return 0;
            }
            finally
            {
                if (createdSnapshots)
                {
                    foreach (var s in snaps)
                    {
                        if (!string.IsNullOrEmpty(s.snapshotId))
                        {
                            try { new VssUtils().DeleteSnapshot(s.snapshotId); } catch { }
                            Console.WriteLine($"Deleted snapshot {s.snapshotId} for {s.volume}");
                        }
                    }
                }
            }
        }

        // Quick verification: structure check + sample a subset of chunks (first, last, and every Nth)
    private static bool VerifyQuick(ImageReader r, Action<string>? log, Action<long,long>? progress, int? parallel)
        {
            // Validate header/index already read by ImageReader ctor
            int count = r.Index.Count;
            if (count == 0) return true;
            // Choose a sampling stride based on image size
            int stride = count <= 200 ? 10 : count <= 1000 ? 25 : 50; // verifies ~2-10% depending on size
            var sampleIdxs = new HashSet<int> { 0, count - 1 };
            for (int i = stride; i < count - 1; i += stride) sampleIdxs.Add(i);

            long total = 0; foreach (var i in sampleIdxs) total += r.Index[i].CompressedLength;
            long done = 0;

            // We can reuse the parallel verify by creating a filtered read loop
            using var sha = System.Security.Cryptography.SHA256.Create();
            int workerCount = Math.Max(1, parallel.HasValue ? parallel.Value : Environment.ProcessorCount);
            int bounded = workerCount * 2;
            var queue = new System.Collections.Concurrent.BlockingCollection<(int idx, int ulen, byte[] expectedHash, byte[] compressed)>(bounded);
            var cts = new System.Threading.CancellationTokenSource();
            bool failed = false;

            var workers = new List<Task>(workerCount);
            for (int w = 0; w < workerCount; w++)
            {
                workers.Add(Task.Run(() =>
                {
                    using var dec = new ZstdSharp.Decompressor();
                    using var s = System.Security.Cryptography.SHA256.Create();
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
                            var actual = s.ComputeHash(decompressed);
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
                            System.Threading.Interlocked.Add(ref done, r.Index[item.idx].CompressedLength);
                            progress?.Invoke(done, total);
                        }
                    }
                }));
            }

            try
            {
                using var fs = new FileStream(((FileStream)typeof(ImageReader).GetField("_fs", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(r)!).Name, FileMode.Open, FileAccess.Read, FileShare.Read);
                foreach (var i in sampleIdxs)
                {
                    var e = r.Index[i];
                    long headerPos = e.FileOffset - ImageFormat.ChunkHeaderSize;
                    fs.Seek(headerPos, SeekOrigin.Begin);
                    using var br = new BinaryReader(fs, Encoding.UTF8, leaveOpen: true);
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

        // Returns a function that supplies desired parallelism, optionally reading from a file.
        // If path is null or does not exist, returns a constant provider of the initial value or CPU count.
        private static Func<int>? BuildParallelProvider(int? initial, string? filePath, string? pipeName = null)
        {
            int fallback = Math.Max(1, initial ?? Environment.ProcessorCount);
            // currentValue is updated by pipe server; file polling is read-on-demand
            int currentValue = fallback;
            if (!string.IsNullOrWhiteSpace(pipeName))
            {
                // Start background named pipe server that accepts plain integer (e.g., "8") updates
                Task.Run(async () =>
                {
                    while (true)
                    {
                        try
                        {
#pragma warning disable CA1416
                            using var server = new System.IO.Pipes.NamedPipeServerStream(pipeName, System.IO.Pipes.PipeDirection.In, 1, System.IO.Pipes.PipeTransmissionMode.Byte, System.IO.Pipes.PipeOptions.Asynchronous);
                            await server.WaitForConnectionAsync();
                            using var reader = new StreamReader(server, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 256, leaveOpen: false);
                            string? line = await reader.ReadLineAsync();
                            if (!string.IsNullOrWhiteSpace(line) && int.TryParse(line.Trim(), out int val))
                            {
                                int max = Math.Max(1, Environment.ProcessorCount * 2);
                                int clamped = Math.Max(1, Math.Min(val, max));
                                System.Threading.Volatile.Write(ref currentValue, clamped);
                            }
#pragma warning restore CA1416
                        }
                        catch
                        {
                            await Task.Delay(500);
                        }
                    }
                });
            }
            if (string.IsNullOrWhiteSpace(filePath))
            {
                return () => System.Threading.Volatile.Read(ref currentValue);
            }
            string fullPath;
            try { fullPath = Path.GetFullPath(filePath); }
            catch { fullPath = filePath!; }
            return () =>
            {
                try
                {
                    if (File.Exists(fullPath))
                    {
                        var txt = File.ReadAllText(fullPath).Trim();
                        if (int.TryParse(txt, out int val))
                        {
                            int max = Math.Max(1, Environment.ProcessorCount * 2);
                            return Math.Max(1, Math.Min(val, max));
                        }
                    }
                }
                catch { }
                return System.Threading.Volatile.Read(ref currentValue);
            };
        }
    }
}
