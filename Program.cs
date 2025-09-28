using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Security.Principal;
using System.Threading.Tasks;
using System.Text;
using System.Collections.Generic;
using System.Reflection;
using DiscUtils.Ntfs;

namespace ImagingUtility
{
    internal class Program
    {
        static async Task<int> Main(string[] args)
        {
            // Enable plain progress output if requested via flag (in addition to env var or redirected output)
            if (args.Any(a => a.Equals("--plain", StringComparison.OrdinalIgnoreCase)))
            {
                ConsoleProgressScope.ForcePlain = true;
            }
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
                    bool writeThrough = false; // default OFF (opt-in)
                    if (Array.Exists(args, a => a.Equals("--write-through", StringComparison.OrdinalIgnoreCase))) writeThrough = true;
                    else if (Array.Exists(args, a => a.Equals("--no-write-through", StringComparison.OrdinalIgnoreCase))) writeThrough = false;
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
                    // Dynamic defaults: target total threads ~4 when available -> parallel=2, pipelineDepth=2
                    var pipeDepthArg = GetArgValue(args, "--pipeline-depth");
                    int pipelineDepth;
                    int effectiveParallel;
                    ComputeParallelDefaults(parallel, pipeDepthArg, out effectiveParallel, out pipelineDepth);

                    // Multi-volume mode
                    if (!string.IsNullOrEmpty(volumesArg) && !string.IsNullOrEmpty(outDirArg))
                    {
                        var getDesiredMv = BuildParallelProvider(effectiveParallel, parCtlFile, parCtlPipe);
                        return await ImageMultipleVolumes(volumesArg, outDirArg, useVss, resume, maxBytes, usedOnly, chunkSize, effectiveParallel, getDesiredMv, writeThrough, pipelineDepth);
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

                        // If not using VSS and the user passed a drive letter (e.g., D: or D:\), map to raw device \\.\D:
                        if (!useVss)
                        {
                            string d = deviceToRead.Trim();
                            if (!d.StartsWith(@"\\.\"))
                            {
                                // Accept D:, D:\, or plain 'D' (be generous)
                                if (d.Length >= 2 && d[1] == ':') d = d.Substring(0, 2);
                                else if (d.Length == 1 && char.IsLetter(d[0])) d = d + ":";
                                if (d.Length == 2 && char.IsLetter(d[0]) && d[1] == ':')
                                {
                                    deviceToRead = @"\\.\" + char.ToUpperInvariant(d[0]) + ":";
                                }
                            }
                        }

                        Console.WriteLine($"Imaging device {deviceToRead} to {outArg} ...");
                        var overallSw = System.Diagnostics.Stopwatch.StartNew();
                        using var reader = new RawDeviceReader(deviceToRead);
                        // If resume and output exists with a valid footer, resume from last chunk
                        var append = resume && File.Exists(outArg);
                        List<IndexEntry>? existingIndex = null;
                        long startOffset = 0;
                        FileMode mode = append ? FileMode.Open : FileMode.Create;
                        var fopts = writeThrough ? FileOptions.WriteThrough : FileOptions.None;
                        using var outStream = new FileStream(outArg, mode, FileAccess.ReadWrite, FileShare.None, 4096, fopts);
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

                        int finalChunk = chunkSize ?? ResolveDefaultChunkSize(effectiveParallel, pipelineDepth);
                        var writer = new ChunkedZstdWriter(outStream, reader.SectorSize, finalChunk, append: append, existingIndex: existingIndex, deviceLength: reader.TotalSize);
                        Func<int>? getDesired = BuildParallelProvider(effectiveParallel, parCtlFile, parCtlPipe);
                        using (var p = new ConsoleProgressScope($"Imaging {deviceToRead}"))
                        {
                            bool canUsedOnly = usedOnly && reader.TryGetNtfsBytesPerCluster(out _);
                            if (canUsedOnly && startOffset == 0)
                            {
                                writer.WriteAllocatedOnly(reader, (done, total) => p.Report(done, total), effectiveParallel, getDesired, pipelineDepth);
                            }
                            else
                            {
                                if (canUsedOnly && startOffset > 0)
                                    Console.WriteLine("[info] Resume with --used-only not yet optimized; falling back to full-range resume for correctness.");
                                var res = await writer.WriteFromAsync(reader, startOffset, maxBytes, (done, total) => p.Report(done, total), effectiveParallel, getDesired, pipelineDepth);
                                Console.WriteLine($"Wrote {res.chunksWritten} chunks; last device offset: {res.lastDeviceOffset}");
                            }
                        }

                        overallSw.Stop();
                        double secs = Math.Max(0.001, overallSw.Elapsed.TotalSeconds);
                        long totalRaw = 0;
                        try
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
                else if (cmd == "export-range")
                {
                    // export-range --in <image-file> --offset <bytes> --length <bytes> [--out <file>]
                    var inArg = GetArgValue(args, "--in");
                    var offArg = GetArgValue(args, "--offset");
                    var lenArg = GetArgValue(args, "--length");
                    var outArg = GetArgValue(args, "--out");
                    if (string.IsNullOrEmpty(inArg) || !File.Exists(inArg) || string.IsNullOrEmpty(offArg) || string.IsNullOrEmpty(lenArg) ||
                        !long.TryParse(offArg, out var offset) || !long.TryParse(lenArg, out var length) || offset < 0 || length < 0)
                    {
                        Console.Error.WriteLine("Usage: export-range --in <image-file> --offset <bytes> --length <bytes> [--out <file>]");
                        return 1;
                    }
                    Console.WriteLine($"Exporting {length} bytes from offset {offset}...");
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    using var rai = new RandomAccessImage(inArg);
                    if (string.IsNullOrEmpty(outArg))
                    {
                        // stdout
                        byte[] buffer = new byte[1024 * 1024];
                        long remaining = length;
                        long pos = offset;
                        using var stdout = Console.OpenStandardOutput();
                        while (remaining > 0)
                        {
                            int toRead = (int)Math.Min(buffer.Length, remaining);
                            rai.ReadAsync(pos, buffer, 0, toRead).GetAwaiter().GetResult();
                            stdout.Write(buffer, 0, toRead);
                            pos += toRead; remaining -= toRead;
                        }
                    }
                    else
                    {
                        using var outFs = new FileStream(outArg, FileMode.Create, FileAccess.Write, FileShare.Read);
                        byte[] buffer = new byte[1024 * 1024];
                        long remaining = length;
                        long pos = offset;
                        while (remaining > 0)
                        {
                            int toRead = (int)Math.Min(buffer.Length, remaining);
                            rai.ReadAsync(pos, buffer, 0, toRead).GetAwaiter().GetResult();
                            outFs.Write(buffer, 0, toRead);
                            pos += toRead; remaining -= toRead;
                        }
                    }
                    sw.Stop();
                    Console.WriteLine($"Done. Elapsed: {ConsoleProgressScope.FormatTime(sw.Elapsed)}");
                    return 0;
                }
                else if (cmd == "ntfs-extract")
                {
                    // ntfs-extract --in <image-file> --offset <bytes> --out-dir <dir> [--path <relative>] [--list-only]
                    // or: ntfs-extract --set-dir <set> --partition <N|Letter> --out-dir <dir> [--path <relative>] [--list-only]
                    var setDirArg = GetArgValue(args, "--set-dir");
                    var partArg = GetArgValue(args, "--partition");
                    var inArg = GetArgValue(args, "--in");
                    var offArg = GetArgValue(args, "--offset");
                    var outDir = GetArgValue(args, "--out-dir");
                    var relPath = GetArgValue(args, "--path");
                    bool listOnly = Array.Exists(args, a => a.Equals("--list-only", StringComparison.OrdinalIgnoreCase));
                    string imgPath = inArg ?? string.Empty;
                    long volOffset = -1;
                    if (!string.IsNullOrEmpty(setDirArg) && !string.IsNullOrEmpty(partArg))
                    {
                        if (!TryResolvePartitionFromSet(setDirArg!, partArg!, out imgPath, out volOffset))
                        {
                            Console.Error.WriteLine("Could not resolve partition from set. Ensure backup.manifest.json exists and the partition index/letter is valid.");
                            return 1;
                        }
                    }
                    if (string.IsNullOrEmpty(imgPath) || !File.Exists(imgPath))
                    {
                        Console.Error.WriteLine("Usage: ntfs-extract --in <image-file> --offset <partition-start-bytes> --out-dir <dir> [--path <relative>] [--list-only]\n   or: ntfs-extract --set-dir <set> --partition <N|Letter> --out-dir <dir> [--path <relative>] [--list-only]");
                        return 1;
                    }
                    if (volOffset < 0)
                    {
                        if (string.IsNullOrEmpty(offArg) || !long.TryParse(offArg, out volOffset) || volOffset < 0)
                        {
                            Console.Error.WriteLine("Provide --offset or use --set-dir with --partition.");
                            return 1;
                        }
                    }
                    if (string.IsNullOrEmpty(outDir))
                    {
                        Console.Error.WriteLine("Usage: ntfs-extract --out-dir <dir> plus either (--in + --offset) or (--set-dir + --partition)");
                        return 1;
                    }
                    Directory.CreateDirectory(outDir);
                    using var rai = new RandomAccessImage(imgPath);
                    using var win = new ImageWindowStream(rai, volOffset);
#pragma warning disable CA1416
                    var ntfs = new NtfsFileSystem(win);
#pragma warning restore CA1416
                    if (listOnly)
                    {
                        foreach (var e in ntfs.GetFiles("\\", "*", System.IO.SearchOption.AllDirectories))
                            Console.WriteLine(e);
                        return 0;
                    }
                    if (!string.IsNullOrEmpty(relPath))
                    {
                        string src = relPath.Replace('/', '\\');
                        if (!src.StartsWith("\\")) src = "\\" + src;
                        if (ntfs.FileExists(src))
                        {
                            string dest = Path.Combine(outDir, Path.GetFileName(src));
                            using var inStream = ntfs.OpenFile(src, FileMode.Open, FileAccess.Read);
                            using var outFs = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.Read);
                            inStream.CopyTo(outFs);
                            Console.WriteLine($"Extracted file to {dest}");
                            return 0;
                        }
                        else if (ntfs.DirectoryExists(src))
                        {
                            ImagingUtility.NtfsExtractHelpers.ExtractNtfsDirectory(ntfs, src, outDir);
                            Console.WriteLine($"Extracted directory to {outDir}");
                            return 0;
                        }
                        else
                        {
                            Console.Error.WriteLine($"Path not found in NTFS: {src}");
                            return 2;
                        }
                    }
                    else
                    {
                        // Extract entire volume tree (careful: could be large)
                        ImagingUtility.NtfsExtractHelpers.ExtractNtfsDirectory(ntfs, "\\", outDir);
                        Console.WriteLine($"Extracted NTFS volume to {outDir}");
                        return 0;
                    }
                }
                else if (cmd == "backup-disk")
                {
                    // backup-disk --disk N --out-dir <dir> [--use-vss]
                    var diskArg = GetArgValue(args, "--disk");
                    var outDirArg = GetArgValue(args, "--out-dir");
                    var useVss = Array.Exists(args, a => a.Equals("--use-vss", StringComparison.OrdinalIgnoreCase));
                    bool writeThrough = false; // default OFF (opt-in)
                    if (Array.Exists(args, a => a.Equals("--write-through", StringComparison.OrdinalIgnoreCase))) writeThrough = true;
                    else if (Array.Exists(args, a => a.Equals("--no-write-through", StringComparison.OrdinalIgnoreCase))) writeThrough = false;
                    int? parallel = null;
                    var parArg = GetArgValue(args, "--parallel");
                    if (!string.IsNullOrEmpty(parArg) && int.TryParse(parArg, out var pval) && pval > 0)
                        parallel = pval;
                    var pipeDepthArg = GetArgValue(args, "--pipeline-depth");
                    int pipelineDepth;
                    int effectiveParallel;
                    ComputeParallelDefaults(parallel, pipeDepthArg, out effectiveParallel, out pipelineDepth);
                    var parCtlFileB = GetArgValue(args, "--parallel-control-file");
                    var parCtlPipeB = GetArgValue(args, "--parallel-control-pipe");
                    if (string.IsNullOrEmpty(diskArg) || string.IsNullOrEmpty(outDirArg) || !int.TryParse(diskArg, out var diskNum))
                    {
                        Console.Error.WriteLine("Usage: backup-disk --disk N --out-dir <dir> [--use-vss] [--parallel N] [--pipeline-depth N] [--write-through]");
                        return 1;
                    }
                    Console.WriteLine($"Building backup set for disk {diskNum} -> {outDirArg} (useVss={useVss}) ...");
                    var swBackup = System.Diagnostics.Stopwatch.StartNew();
                    var getDesiredB = BuildParallelProvider(effectiveParallel, parCtlFileB, parCtlPipeB);
                    await BackupSetBuilder.BuildBackupSetAsync(diskNum, outDirArg, useVss, effectiveParallel, getDesiredB, pipelineDepth, writeThrough);
                    swBackup.Stop();
                    Console.WriteLine($"Backup set complete. Elapsed: {ConsoleProgressScope.FormatTime(swBackup.Elapsed)}");
                    return 0;
                }
                else if (cmd == "verify-set")
                {
                    // verify-set --set-dir <dir> [--quick] [--parallel N]
                    var setDir = GetArgValue(args, "--set-dir");
                    if (string.IsNullOrEmpty(setDir) || !Directory.Exists(setDir))
                    {
                        Console.Error.WriteLine("Usage: verify-set --set-dir <dir> [--quick] [--parallel N] [--plain]");
                        return 1;
                    }
                    bool quick = Array.Exists(args, a => a.Equals("--quick", StringComparison.OrdinalIgnoreCase));
                    int? parallel = null; var parArg = GetArgValue(args, "--parallel");
                    if (!string.IsNullOrEmpty(parArg) && int.TryParse(parArg, out var pval) && pval > 0) parallel = pval;
                    string manifestPath = Path.Combine(setDir, "backup.manifest.json");
                    if (!File.Exists(manifestPath))
                    {
                        Console.Error.WriteLine($"Manifest not found: {manifestPath}");
                        return 2;
                    }
                    var manifest = BackupSetIO.Load(manifestPath);
                    bool allOk = true;
                    // Verify partition table dump hash if available
                    if (!string.IsNullOrEmpty(manifest.PartitionTableDump) && !string.IsNullOrEmpty(manifest.PartitionTableDumpSha256))
                    {
                        var ptPath = Path.Combine(setDir, manifest.PartitionTableDump);
                        if (File.Exists(ptPath))
                        {
                            var actual = ComputeFileSha256Safe(ptPath);
                            if (!string.IsNullOrEmpty(actual) && !actual.Equals(manifest.PartitionTableDumpSha256, StringComparison.OrdinalIgnoreCase))
                            {
                                Console.Error.WriteLine($"Partition table dump hash mismatch: {Path.GetFileName(ptPath)}");
                                allOk = false;
                            }
                        }
                        else
                        {
                            Console.Error.WriteLine($"Partition table dump missing: {Path.GetFileName(ptPath)}");
                            allOk = false;
                        }
                    }
                    // Verify each partition artifact
                    foreach (var part in manifest.Partitions)
                    {
                        if (!string.IsNullOrEmpty(part.ImageFile))
                        {
                            var imgPath = Path.Combine(setDir, part.ImageFile);
                            if (!File.Exists(imgPath)) { Console.Error.WriteLine($"Missing image: {part.ImageFile}"); allOk = false; continue; }
                            // Optional pre-check: ImageSha256 if recorded (single-file hash can be large; skip if not present)
                            if (!string.IsNullOrEmpty(part.ImageSha256))
                            {
                                var imgHash = ComputeFileSha256Safe(imgPath);
                                if (!string.IsNullOrEmpty(imgHash) && !imgHash.Equals(part.ImageSha256, StringComparison.OrdinalIgnoreCase))
                                { Console.Error.WriteLine($"Hash mismatch: {part.ImageFile}"); allOk = false; }
                            }
                            using (var p = new ConsoleProgressScope($"Verifying {Path.GetFileName(imgPath)}"))
                            {
                                var r = new ImageReader(imgPath);
                                bool ok = quick ? VerifyQuick(r, msg => Console.WriteLine(msg), (done, total) => p.Report(done, total), parallel)
                                                : r.VerifyAll(msg => Console.WriteLine(msg), (done, total) => p.Report(done, total), parallel);
                                if (!ok) allOk = false;
                                p.Complete();
                                Console.WriteLine(ok ? $"Verify OK: {Path.GetFileName(imgPath)}" : $"Verify FAILED: {Path.GetFileName(imgPath)}");
                            }
                        }
                        else if (!string.IsNullOrEmpty(part.RawDump))
                        {
                            var dumpPath = Path.Combine(setDir, part.RawDump);
                            if (!File.Exists(dumpPath)) { Console.Error.WriteLine($"Missing raw dump: {part.RawDump}"); allOk = false; continue; }
                            if (!string.IsNullOrEmpty(part.RawDumpSha256))
                            {
                                var actual = ComputeFileSha256Safe(dumpPath);
                                if (!string.IsNullOrEmpty(actual) && !actual.Equals(part.RawDumpSha256, StringComparison.OrdinalIgnoreCase))
                                {
                                    Console.Error.WriteLine($"Hash mismatch: {part.RawDump}");
                                    allOk = false;
                                }
                                else
                                {
                                    Console.WriteLine($"Raw dump OK: {Path.GetFileName(dumpPath)}");
                                }
                            }
                            else
                            {
                                Console.WriteLine($"Raw dump present (no hash recorded): {Path.GetFileName(dumpPath)}");
                            }
                        }
                    }
                    Console.WriteLine(allOk ? "Backup set verification: OK" : "Backup set verification: FAILED");
                    return allOk ? 0 : 4;
                }
                else if (cmd == "ntfs-serve")
                {
                    // ntfs-serve --in <image-file> --offset <bytes> [--host 127.0.0.1] [--port 18080]
                    // or: ntfs-serve --set-dir <set> --partition <N|Letter> [--host] [--port]
                    var setDirArg = GetArgValue(args, "--set-dir");
                    var partArg = GetArgValue(args, "--partition");
                    var inArg = GetArgValue(args, "--in");
                    var offArg = GetArgValue(args, "--offset");
                    var host = GetArgValue(args, "--host") ?? "127.0.0.1";
                    var portArg = GetArgValue(args, "--port");
                    string imgPath = inArg ?? string.Empty;
                    long volOffset = -1;
                    if (!string.IsNullOrEmpty(setDirArg) && !string.IsNullOrEmpty(partArg))
                    {
                        if (!TryResolvePartitionFromSet(setDirArg!, partArg!, out imgPath, out volOffset))
                        {
                            Console.Error.WriteLine("Could not resolve partition from set. Ensure backup.manifest.json exists and the partition index/letter is valid.");
                            return 1;
                        }
                    }
                    if (string.IsNullOrEmpty(imgPath) || !File.Exists(imgPath))
                    {
                        Console.Error.WriteLine("Usage: ntfs-serve --in <image-file> --offset <partition-start-bytes> [--host 127.0.0.1] [--port 18080]\n   or: ntfs-serve --set-dir <set> --partition <N|Letter> [--host] [--port]");
                        return 1;
                    }
                    if (volOffset < 0)
                    {
                        if (string.IsNullOrEmpty(offArg) || !long.TryParse(offArg, out volOffset) || volOffset < 0)
                        {
                            Console.Error.WriteLine("Provide --offset or use --set-dir with --partition.");
                            return 1;
                        }
                    }
                    int port = 18080; if (!string.IsNullOrEmpty(portArg) && int.TryParse(portArg, out var p)) port = p;
                    using var cts = new System.Threading.CancellationTokenSource();
                    Console.CancelKeyPress += (s, e) => { e.Cancel = true; cts.Cancel(); };
                    await ImagingUtility.NtfsHttpServer.ServeAsync(host, port, imgPath, volOffset, cts.Token);
                    return 0;
                }
                else if (cmd == "ntfs-webdav")
                {
                    // ntfs-webdav --in <image-file> --offset <bytes> [--host 127.0.0.1] [--port 18081]
                    // or: ntfs-webdav --set-dir <set> --partition <N|Letter> [--host] [--port]
                    var setDirArg = GetArgValue(args, "--set-dir");
                    var partArg = GetArgValue(args, "--partition");
                    var inArg = GetArgValue(args, "--in");
                    var offArg = GetArgValue(args, "--offset");
                    var host = GetArgValue(args, "--host") ?? "127.0.0.1";
                    var portArg = GetArgValue(args, "--port");
                    string imgPath = inArg ?? string.Empty;
                    long volOffset = -1;
                    if (!string.IsNullOrEmpty(setDirArg) && !string.IsNullOrEmpty(partArg))
                    {
                        if (!TryResolvePartitionFromSet(setDirArg!, partArg!, out imgPath, out volOffset))
                        {
                            Console.Error.WriteLine("Could not resolve partition from set. Ensure backup.manifest.json exists and the partition index/letter is valid.");
                            return 1;
                        }
                    }
                    if (string.IsNullOrEmpty(imgPath) || !File.Exists(imgPath))
                    {
                        Console.Error.WriteLine("Usage: ntfs-webdav --in <image-file> --offset <partition-start-bytes> [--host 127.0.0.1] [--port 18081]\n   or: ntfs-webdav --set-dir <set> --partition <N|Letter> [--host] [--port]");
                        return 1;
                    }
                    if (volOffset < 0)
                    {
                        if (string.IsNullOrEmpty(offArg) || !long.TryParse(offArg, out volOffset) || volOffset < 0)
                        {
                            Console.Error.WriteLine("Provide --offset or use --set-dir with --partition.");
                            return 1;
                        }
                    }
                    int port = 18081; if (!string.IsNullOrEmpty(portArg) && int.TryParse(portArg, out var p)) port = p;
                    using var cts = new System.Threading.CancellationTokenSource();
                    Console.CancelKeyPress += (s, e) => { e.Cancel = true; cts.Cancel(); };
                    await ImagingUtility.NtfsWebDavServer.ServeAsync(host, port, imgPath, volOffset, cts.Token);
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
                else if (cmd == "serve-proxy")
                {
                    // serve-proxy --in <image-file> [--host 127.0.0.1] [--port 11459] [--pipe NAME] [--cache-chunks 4] [--offset N] [--length N]
                    // or: serve-proxy --set-dir <set> --partition <N|Letter> [...]
                    var setDirArg = GetArgValue(args, "--set-dir");
                    var partArg = GetArgValue(args, "--partition");
                    var inArg = GetArgValue(args, "--in");
                    if (string.IsNullOrEmpty(inArg) && (string.IsNullOrEmpty(setDirArg) || string.IsNullOrEmpty(partArg)))
                    {
                        Console.Error.WriteLine("Usage: serve-proxy --in <image-file> [--host 127.0.0.1] [--port 11459] [--pipe NAME] [--cache-chunks 4] [--offset N] [--length N]\n   or: serve-proxy --set-dir <set> --partition <N|Letter> [...]");
                        return 1;
                    }
                    string imgPath = inArg ?? string.Empty;
                    var host = GetArgValue(args, "--host") ?? "127.0.0.1";
                    var portArg = GetArgValue(args, "--port");
                    var pipeName = GetArgValue(args, "--pipe");
                    var cacheArg = GetArgValue(args, "--cache-chunks");
                    var offArg = GetArgValue(args, "--offset");
                    var lenArg = GetArgValue(args, "--length");
                    long? resolvedOffset = null;
                    long? resolvedLength = null;
                    if (!string.IsNullOrEmpty(setDirArg) && !string.IsNullOrEmpty(partArg))
                    {
                        if (!TryResolvePartitionFromSet(setDirArg!, partArg!, out imgPath, out var voff, out var vlen))
                        {
                            Console.Error.WriteLine("Could not resolve partition from set. Ensure backup.manifest.json exists and the partition index/letter is valid.");
                            return 1;
                        }
                        resolvedOffset = voff;
                        resolvedLength = vlen;
                    }
                    int port = 11459; if (!string.IsNullOrEmpty(portArg) && int.TryParse(portArg, out var pval)) port = pval;
                    int cache = 4; if (!string.IsNullOrEmpty(cacheArg) && int.TryParse(cacheArg, out var cval) && cval > 0) cache = cval;
                    long? devOff = resolvedOffset; if (devOff == null && !string.IsNullOrEmpty(offArg) && long.TryParse(offArg, out var o)) devOff = Math.Max(0, o);
                    long? devLen = resolvedLength; if (devLen == null && !string.IsNullOrEmpty(lenArg) && long.TryParse(lenArg, out var l)) devLen = Math.Max(0, l);
                    if (!string.IsNullOrEmpty(imgPath)) inArg = imgPath;
                    if (string.IsNullOrEmpty(inArg) || !File.Exists(inArg))
                    {
                        Console.Error.WriteLine("Input image not found after resolving partition manifest.");
                        return 1;
                    }
                    using var cts = new System.Threading.CancellationTokenSource();
                    Console.CancelKeyPress += (s, e) => { e.Cancel = true; cts.Cancel(); };
                    if (!string.IsNullOrWhiteSpace(pipeName))
                    {
                        Console.WriteLine($"Starting read-only proxy on pipe://{pipeName} for {inArg} ...");
                        await DevioProxyServer.ServePipeAsync(pipeName!, inArg, cache, devOff, devLen, cts.Token);
                        return 0;
                    }
                    Console.WriteLine($"Starting read-only proxy on tcp://{host}:{port} for {inArg} ...");
                    await DevioProxyServer.ServeTcpAsync(host, port, inArg, cache, devOff, devLen, cts.Token);
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

        // Decide defaults for parallel and pipeline-depth.
        // Goal: total threads ~4 when available -> parallel=2, pipelineDepth=2.
        // If cores limited, split as evenly as possible with minimum of 1.
        private static void ComputeParallelDefaults(int? requestedParallel, string? pipelineDepthArg, out int effectiveParallel, out int pipelineDepth)
        {
            // If user explicitly set parallel, use it (clamp to >=1)
            int cores = Math.Max(1, Environment.ProcessorCount);
            effectiveParallel = Math.Max(1, requestedParallel ?? 0);
            // If not provided, choose based on cores
            if (effectiveParallel == 0)
            {
                if (cores >= 4) effectiveParallel = 2; // leave room for writer thread and I/O
                else if (cores == 3) effectiveParallel = 2;
                else effectiveParallel = 1;
            }
            // Pipeline depth: if provided, honor it; else pick to target ~4 total concurrency
            if (!string.IsNullOrEmpty(pipelineDepthArg) && int.TryParse(pipelineDepthArg, out var pd) && pd >= 1 && pd <= 8)
            {
                pipelineDepth = pd;
            }
            else
            {
                int targetTotal = 4;
                pipelineDepth = Math.Max(1, Math.Min(8, targetTotal / Math.Max(1, effectiveParallel)));
                if (effectiveParallel * pipelineDepth < targetTotal && pipelineDepth < 8)
                    pipelineDepth = Math.Min(8, pipelineDepth + 1);
            }
        }

        // Default chunk size selection: prefer 512 MiB, but fallback to 64 MiB if memory is constrained.
        // Heuristic: require at least (parallel * pipelineDepth * chunk + margin) free memory.
        private static int ResolveDefaultChunkSize(int effectiveParallel, int pipelineDepth)
        {
            const long preferred = 512L * 1024 * 1024; // 512 MiB
            const long fallback = 64L * 1024 * 1024;   // 64 MiB
            long neededPreferred = (long)effectiveParallel * pipelineDepth * preferred + (128L * 1024 * 1024);
            long free = GetApproxAvailableMemory();
            if (free <= 0)
                return (int)preferred; // if unknown, be optimistic
            return free >= neededPreferred ? (int)preferred : (int)fallback;
        }

        private static long GetApproxAvailableMemory()
        {
            try
            {
                MEMORYSTATUSEX mem = new MEMORYSTATUSEX();
                if (GlobalMemoryStatusEx(mem))
                {
                    ulong avail = mem.ullAvailPhys;
                    return (long)Math.Min(avail, long.MaxValue);
                }
            }
            catch { }
            return 0;
        }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Auto)]
        private class MEMORYSTATUSEX
        {
            public uint dwLength = (uint)System.Runtime.InteropServices.Marshal.SizeOf(typeof(MEMORYSTATUSEX));
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
        }

        [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto, SetLastError = true)]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        private static extern bool GlobalMemoryStatusEx([System.Runtime.InteropServices.In] MEMORYSTATUSEX lpBuffer);

    static void PrintHelp()
        {
            Console.WriteLine("ImagingUtility - simple prototype\n");
            Console.WriteLine("Commands:");
            Console.WriteLine("  list\t\tList physical drives");
            Console.WriteLine("  image --device \\ \\ \\ . \\ \\ PhysicalDriveN|C: --out <file> [--use-vss] [--resume] [--all-blocks] [--chunk-size N] [--max-bytes N] [--parallel N] [--pipeline-depth N] [--write-through] [--parallel-control-file PATH] [--parallel-control-pipe NAME] [--plain]\tCreate or resume a chunked image (defaults: --used-only, 512M chunk with 64M fallback, write-through OFF, dynamic parallel/pipeline targeting ~4 total)".Replace(" ", string.Empty));
            Console.WriteLine("  image --volumes C:,D: --out-dir <dir> [--use-vss] [--resume] [--all-blocks] [--chunk-size N] [--max-bytes N] [--parallel N] [--pipeline-depth N] [--write-through] [--parallel-control-file PATH] [--parallel-control-pipe NAME] [--plain]\tSnapshot multiple volumes (defaults: --used-only, 512M chunk with 64M fallback, write-through OFF)");
            Console.WriteLine("  backup-disk --disk N --out-dir <dir> [--use-vss] [--parallel N] [--pipeline-depth N] [--write-through] [--parallel-control-file PATH] [--parallel-control-pipe NAME] [--plain]\tCreate a full-disk backup set (manifest + per-partition images; write-through OFF by default)");
            Console.WriteLine("  verify-set --set-dir <dir> [--quick] [--parallel N] [--plain]\tVerify a full backup set: manifest, raw-dump hashes, and per-image verification");
            Console.WriteLine("  restore-set --set-dir <dir> --out-raw <file>\tReconstruct a raw disk image from a backup set");
            Console.WriteLine("  restore-physical --set-dir <dir> --disk N\tWrite a backup set directly to a physical disk (DESTRUCTIVE)");
            Console.WriteLine("  verify --in <file> [--parallel N] [--quick] [--plain]\tVerify chunk checksums and integrity (use --quick for faster sampling-based verify)");
            Console.WriteLine("  export-raw --in <image> --out <raw>\tExport compressed image to raw disk file");
            Console.WriteLine("  export-vhd --in <image> --out <vhd>\tExport compressed image to fixed VHD (mountable in Windows)");
            Console.WriteLine("  export-range --in <image> --offset <bytes> --length <bytes> [--out <file>]\tRead an arbitrary range directly from a compressed image");
            Console.WriteLine("  ntfs-extract --in <image> --offset <partition-start-bytes> --out-dir <dir> [--path <relative>] [--list-only]\tBrowse or extract files using a userspace NTFS reader (bypasses ACLs; read-only)");
            Console.WriteLine("             or: ntfs-extract --set-dir <set> --partition <N|Letter> --out-dir <dir> [--path <rel>] [--list-only]");
            Console.WriteLine("  ntfs-serve --in <image> --offset <partition-start-bytes> [--host 127.0.0.1] [--port 18080]\tHTTP browse/download for NTFS content via userspace reader (bypasses ACLs; read-only)");
            Console.WriteLine("             or: ntfs-serve --set-dir <set> --partition <N|Letter> [--host] [--port]");
            Console.WriteLine("  ntfs-webdav --in <image> --offset <partition-start-bytes> [--host 127.0.0.1] [--port 18081]\tRead-only WebDAV view; map with 'net use Z: http://host:port/'");
            Console.WriteLine("             or: ntfs-webdav --set-dir <set> --partition <N|Letter> [--host] [--port]");
            Console.WriteLine("  serve-proxy --in <image> [--host 127.0.0.1] [--port 11459] [--pipe NAME] [--cache-chunks 4] [--offset N] [--length N]\tServe a read-only block device over TCP or named pipe for DevIO/Proxy-compatible clients; optionally window a partition with --offset/--length");
            Console.WriteLine("             or: serve-proxy --set-dir <set> --partition <N|Letter> [...]");
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

        private static string ComputeFileSha256Safe(string path)
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                using var sha = System.Security.Cryptography.SHA256.Create();
                var hash = sha.ComputeHash(fs);
                return Convert.ToHexString(hash).ToLowerInvariant();
            }
            catch
            {
                return string.Empty;
            }
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

        // Resolve a partition from a backup set manifest by index or drive letter.
        // Returns true if successful, providing an image path and volume start offset (and optional length).
        private static bool TryResolvePartitionFromSet(string setDir, string partitionSelector, out string imagePath, out long volumeOffset)
            => TryResolvePartitionFromSet(setDir, partitionSelector, out imagePath, out volumeOffset, out _);

        private static bool TryResolvePartitionFromSet(string setDir, string partitionSelector, out string imagePath, out long volumeOffset, out long? volumeLength)
        {
            imagePath = string.Empty; volumeOffset = -1; volumeLength = null;
            try
            {
                string manifestPath = Path.Combine(setDir, "backup.manifest.json");
                if (!File.Exists(manifestPath)) return false;
                var manifest = BackupSetIO.Load(manifestPath);
                PartitionEntry? match = null;
                // Partition by index (1-based)
                if (int.TryParse(partitionSelector.TrimEnd(':'), out var idx))
                {
                    match = manifest.Partitions.Find(p => p.Index == idx);
                }
                else
                {
                    // Try drive letter: 'C' or 'C:'
                    string letter = partitionSelector.Trim();
                    if (letter.Length >= 1 && char.IsLetter(letter[0]))
                    {
                        letter = char.ToUpperInvariant(letter[0]).ToString();
                        match = manifest.Partitions.Find(p => (p.DriveLetter ?? string.Empty).Equals(letter, StringComparison.OrdinalIgnoreCase));
                    }
                }
                if (match == null) return false;
                // Prefer a compressed image file for this partition
                if (!string.IsNullOrEmpty(match.ImageFile))
                {
                    var candidate = Path.Combine(setDir, match.ImageFile);
                    if (!File.Exists(candidate)) return false;
                    imagePath = candidate;
                    volumeOffset = 0;
                    volumeLength = match.Size > 0 ? match.Size : null;
                    return true;
                }
                // Otherwise, if we only have a raw dump file, we still can serve it
                if (!string.IsNullOrEmpty(match.RawDump))
                {
                    var candidate = Path.Combine(setDir, match.RawDump);
                    if (!File.Exists(candidate)) return false;
                    imagePath = candidate;
                    volumeOffset = 0;
                    volumeLength = match.Size > 0 ? match.Size : null;
                    return true;
                }
                // Fallback: If no per-partition image file, try a top-level disk image if present and apply offset/length
                var diskImg = Directory.EnumerateFiles(setDir, "*.skzimg").FirstOrDefault();
                if (diskImg != null)
                {
                    imagePath = diskImg;
                    volumeOffset = Math.Max(0, match.StartingOffset);
                    volumeLength = match.Size > 0 ? match.Size : null;
                    return true;
                }
                return false;
            }
            catch
            {
                return false;
            }
        }

    private static async Task<int> ImageMultipleVolumes(string volumesArg, string outDir, bool useVss, bool resume, long? maxBytes, bool usedOnly, int? chunkSize, int? parallel, Func<int>? getDesired, bool writeThrough, int pipelineDepth)
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
                    var fopts = writeThrough ? FileOptions.WriteThrough : FileOptions.None;
                    using var outStream = new FileStream(outPath, mode, FileAccess.ReadWrite, FileShare.None, 4096, fopts);
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

                    int par = Math.Max(1, parallel ?? Environment.ProcessorCount);
                    int finalChunk = chunkSize ?? ResolveDefaultChunkSize(par, pipelineDepth);
                    var writer = new ChunkedZstdWriter(outStream, reader.SectorSize, finalChunk, append: append, existingIndex: existingIndex, deviceLength: reader.TotalSize);
                    using (var p = new ConsoleProgressScope($"Imaging {s.volume}"))
                    {
                        bool canUsedOnly = usedOnly && reader.TryGetNtfsBytesPerCluster(out _);
                        if (canUsedOnly && startOffset == 0)
                        {
                            writer.WriteAllocatedOnly(reader, (done, total) => p.Report(done, total), parallel, getDesired, pipelineDepth);
                        }
                        else
                        {
                            if (canUsedOnly && startOffset > 0)
                                Console.WriteLine("[info] Resume with --used-only not yet optimized; falling back to full-range resume for correctness.");
                            await writer.WriteFromAsync(reader, startOffset, maxBytes, (done, total) => p.Report(done, total), parallel, getDesired, pipelineDepth);
                        }
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

namespace ImagingUtility
{
    internal static class NtfsExtractHelpers
    {
        public static void ExtractNtfsDirectory(DiscUtils.Ntfs.NtfsFileSystem ntfs, string ntfsPath, string destRoot)
        {
            foreach (var dir in ntfs.GetDirectories(ntfsPath))
            {
                var rel = dir.TrimStart('\\');
                var outDir = Path.Combine(destRoot, rel);
                Directory.CreateDirectory(outDir);
                ExtractNtfsDirectory(ntfs, dir, destRoot);
            }
            foreach (var file in ntfs.GetFiles(ntfsPath))
            {
                var rel = file.TrimStart('\\');
                var outPath = Path.Combine(destRoot, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
                using var inStream = ntfs.OpenFile(file, FileMode.Open, FileAccess.Read);
                using var outFs = new FileStream(outPath, FileMode.Create, FileAccess.Write, FileShare.Read);
                inStream.CopyTo(outFs);
            }
        }
    }
}
