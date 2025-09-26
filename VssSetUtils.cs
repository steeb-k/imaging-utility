using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace ImagingUtility
{
    // Creates a consistent VSS snapshot set across multiple volumes using DiskShadow.exe.
    // Returns per-volume snapshot info: (volume, snapshotId, deviceObject).
    internal class VssSetUtils
    {
        public record SnapshotInfo(string Volume, string SnapshotId, string DeviceObject);

        public List<SnapshotInfo> CreateSnapshotSet(IEnumerable<string> volumes)
        {
            var vols = volumes.Select(NormalizeVolume).ToList();
            if (vols.Count == 0) throw new ArgumentException("No volumes supplied");

            // Build DiskShadow script with aliases so we can map outputs reliably.
            var sb = new StringBuilder();
            sb.AppendLine("set context persistent");
            sb.AppendLine("set verbose on");
            sb.AppendLine("begin backup");
            for (int i = 0; i < vols.Count; i++)
            {
                var vol = vols[i];
                var alias = MakeAlias(vol);
                sb.AppendLine($"add volume {EscapeVolume(vol)} alias {alias}");
            }
            sb.AppendLine("create");
            sb.AppendLine("list shadows all");
            sb.AppendLine("end backup");

            var scriptPath = Path.Combine(Path.GetTempPath(), $"diskshadow_{Guid.NewGuid():N}.txt");
            File.WriteAllText(scriptPath, sb.ToString(), Encoding.ASCII);

            try
            {
                var output = RunDiskShadowScript(scriptPath);
                return ParseDiskShadowOutput(output, vols);
            }
            finally
            {
                try { File.Delete(scriptPath); } catch { }
            }
        }

        public void DeleteSnapshots(IEnumerable<string> snapshotIds)
        {
            var ids = snapshotIds.Where(id => !string.IsNullOrWhiteSpace(id)).ToList();
            if (ids.Count == 0) return;

            var sb = new StringBuilder();
            sb.AppendLine("set verbose on");
            foreach (var id in ids)
                sb.AppendLine($"delete shadows id {id}");

            var scriptPath = Path.Combine(Path.GetTempPath(), $"diskshadow_del_{Guid.NewGuid():N}.txt");
            File.WriteAllText(scriptPath, sb.ToString(), Encoding.ASCII);
            try { RunDiskShadowScript(scriptPath); } finally { try { File.Delete(scriptPath); } catch { } }
        }

        private static string RunDiskShadowScript(string scriptPath)
        {
            var psi = new ProcessStartInfo
            {
                FileName = "diskshadow.exe",
                Arguments = $"/s \"{scriptPath}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var p = Process.Start(psi)!;
            string stdout = p.StandardOutput.ReadToEnd();
            string stderr = p.StandardError.ReadToEnd();
            p.WaitForExit();
            if (p.ExitCode != 0)
            {
                throw new InvalidOperationException($"DiskShadow failed (exit {p.ExitCode}). Output:\n{stdout}\nErrors:\n{stderr}");
            }
            return stdout + "\n" + stderr;
        }

        private static List<SnapshotInfo> ParseDiskShadowOutput(string output, List<string> vols)
        {
            // DiskShadow emits blocks per snapshot. When aliases are used, lines contain "Alias: <alias>".
            // We'll search for blocks containing both Alias and Shadow Copy ID and Shadow Copy Volume.
            var list = new List<SnapshotInfo>();
            var blocks = SplitBlocks(output);
            var volByAlias = vols.ToDictionary(MakeAlias, v => v, StringComparer.OrdinalIgnoreCase);

            foreach (var block in blocks)
            {
                var aliasMatch = Regex.Match(block, @"Alias:\s*(\S+)", RegexOptions.IgnoreCase);
                var idMatch = Regex.Match(block, @"Shadow Copy ID:\s*\{([0-9a-fA-F\-]{36})\}", RegexOptions.IgnoreCase);
                var devMatch = Regex.Match(block, @"Shadow Copy Volume:\s*(\\\\\?\\GLOBALROOT\\Device\\[^\r\n]+)", RegexOptions.IgnoreCase);
                if (!aliasMatch.Success || !idMatch.Success || !devMatch.Success) continue;
                var alias = aliasMatch.Groups[1].Value;
                if (!volByAlias.TryGetValue(alias, out var vol)) continue;
                var id = "{" + idMatch.Groups[1].Value + "}";
                var dev = devMatch.Groups[1].Value.Trim();
                list.Add(new SnapshotInfo(vol, id, dev));
            }

            // Ensure we got all requested volumes
            var missing = vols.Except(list.Select(s => s.Volume), StringComparer.OrdinalIgnoreCase).ToList();
            if (missing.Count > 0)
            {
                throw new InvalidOperationException("Failed to create snapshot for: " + string.Join(", ", missing));
            }
            return list;
        }

        private static IEnumerable<string> SplitBlocks(string text)
        {
            // Coarse split on blank lines
            var parts = Regex.Split(text, @"\r?\n\s*\r?\n");
            foreach (var p in parts)
                if (!string.IsNullOrWhiteSpace(p)) yield return p;
        }

        private static string MakeAlias(string vol)
        {
            // alias must be simple, no spaces
            if (vol.Length >= 2 && vol[1] == ':') return (char.ToUpperInvariant(vol[0])).ToString();
            // fall back to sanitized
            var s = new string(vol.Where(char.IsLetterOrDigit).ToArray());
            return string.IsNullOrEmpty(s) ? "VOL" : s.Substring(0, Math.Min(6, s.Length)).ToUpperInvariant();
        }

        private static string EscapeVolume(string vol)
        {
            // DiskShadow expects a path like C:\ or a volume GUID path like \\?\Volume{GUID}\\
            if (vol.Contains(' ')) return '"' + vol + '"';
            return vol;
        }

        private static string NormalizeVolume(string v)
        {
            if (string.IsNullOrEmpty(v)) return v;
            // Ensure trailing backslash for drive letters and volume GUID paths
            if (v.Length == 2 && v[1] == ':') return v + "\\";
            if (v.StartsWith("\\\\?\\Volume{", StringComparison.OrdinalIgnoreCase) && !v.EndsWith("\\")) return v + "\\";
            return v;
        }
    }
}
