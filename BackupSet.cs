using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace ImagingUtility
{
    internal class BackupSetManifest
    {
        public string Version { get; set; } = "1";
        public int DiskNumber { get; set; }
        public long DiskSize { get; set; }
        public string CreatedUtc { get; set; } = DateTime.UtcNow.ToString("o");
        public List<PartitionEntry> Partitions { get; set; } = new();
        public string? PartitionTableDump { get; set; } // relative path to pt dump file, if present
        public string? PartitionTableDumpSha256 { get; set; }
        public string? Notes { get; set; }
    }

    internal class PartitionEntry
    {
        public int Index { get; set; }
        public long StartingOffset { get; set; }
        public long Size { get; set; }
        public string? Type { get; set; }
        public string? DriveLetter { get; set; }
        public string? VolumeLabel { get; set; }
        public string? FileSystem { get; set; }
    public string? ImageFile { get; set; } // relative path to .skzimg for this partition (if NTFS/FAT/exFAT imaged via VSS)
        public string? RawDump { get; set; } // relative path to raw dump file for non-VSS partitions (EFI/MSR/Recovery or unknown FS)
        public string? RawDumpSha256 { get; set; }
        public string? ImageSha256 { get; set; }
    }

    internal static class BackupSetIO
    {
        public static void Save(string path, BackupSetManifest manifest)
        {
            var opts = new JsonSerializerOptions { WriteIndented = true };
            var json = JsonSerializer.Serialize(manifest, opts);
            File.WriteAllText(path, json);
        }

        public static BackupSetManifest Load(string path)
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<BackupSetManifest>(json) ?? throw new InvalidDataException("Invalid manifest");
        }
    }
}
