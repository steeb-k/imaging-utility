using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;

namespace ImagingUtility
{
    internal class DiskLayout
    {
        public int DiskNumber { get; init; }
        public long DiskSize { get; init; }
        public List<PartitionInfo> Partitions { get; init; } = new();
    }

    internal class PartitionInfo
    {
        public int Index { get; init; }
        public long StartingOffset { get; init; }
        public long Size { get; init; }
        public string? Type { get; init; }
        public string? DriveLetter { get; init; }
        public string? VolumeLabel { get; init; }
        public string? FileSystem { get; init; }
        public string? VolumeDeviceId { get; init; }
    }

    internal static class DiskLayoutUtils
    {
        public static DiskLayout GetDiskLayout(int diskNumber)
        {
            long diskSize = 0;
            using (var searcher = new ManagementObjectSearcher($"SELECT Size FROM Win32_DiskDrive WHERE Index = {diskNumber}"))
            using (var results = searcher.Get())
            {
                var drive = results.Cast<ManagementObject>().FirstOrDefault();
                if (drive == null) throw new InvalidOperationException($"Disk {diskNumber} not found");
                diskSize = Convert.ToInt64(drive["Size"]);
            }

            var layout = new DiskLayout { DiskNumber = diskNumber, DiskSize = diskSize };

            var parts = new List<PartitionInfo>();
            using (var searcher = new ManagementObjectSearcher($"SELECT DeviceID, Index, StartingOffset, Size, Type FROM Win32_DiskPartition WHERE DiskIndex = {diskNumber}"))
            using (var results = searcher.Get())
            {
                foreach (ManagementObject p in results)
                {
                    int idx = Convert.ToInt32(p["Index"]);
                    long start = Convert.ToInt64(p["StartingOffset"]);
                    long size = Convert.ToInt64(p["Size"]);
                    string? type = p["Type"]?.ToString();

                    string? driveLetter = null;
                    string? volLabel = null;
                    string? fs = null;
                    string? volDev = null;

                    // Link to logical disk via association
                    string partDeviceId = p["DeviceID"]!.ToString()!; // e.g., "Disk #0, Partition #1"
                    using (var assoc = new ManagementObjectSearcher($"ASSOCIATORS OF {{Win32_DiskPartition.DeviceID='{EscapeWmiPath(partDeviceId)}'}} WHERE AssocClass = Win32_LogicalDiskToPartition"))
                    using (var assocRes = assoc.Get())
                    {
                        var ld = assocRes.Cast<ManagementObject>().FirstOrDefault();
                        if (ld != null)
                        {
                            driveLetter = ld["DeviceID"]?.ToString(); // e.g., "C:"
                            if (!string.IsNullOrEmpty(driveLetter))
                            {
                                using var vSearch = new ManagementObjectSearcher($"SELECT DeviceID, DriveLetter, Label, FileSystem, Capacity FROM Win32_Volume WHERE DriveLetter = '{driveLetter}'");
                                using var vRes = vSearch.Get();
                                var vol = vRes.Cast<ManagementObject>().FirstOrDefault();
                                if (vol != null)
                                {
                                    volLabel = vol["Label"]?.ToString();
                                    fs = vol["FileSystem"]?.ToString();
                                    volDev = vol["DeviceID"]?.ToString(); // e.g., \\?\Volume{GUID}\
                                }
                            }
                        }
                    }

                    parts.Add(new PartitionInfo
                    {
                        Index = idx,
                        StartingOffset = start,
                        Size = size,
                        Type = type,
                        DriveLetter = driveLetter,
                        VolumeLabel = volLabel,
                        FileSystem = fs,
                        VolumeDeviceId = volDev
                    });
                }
            }

            layout.Partitions.AddRange(parts.OrderBy(p => p.StartingOffset));
            return layout;
        }

        private static string EscapeWmiPath(string s)
        {
            return s.Replace("\\", "\\\\").Replace("'", "''");
        }
    }
}
