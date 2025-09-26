using System;
using System.Diagnostics;

namespace ImagingUtility
{
    internal sealed class ConsoleProgressScope : IDisposable
    {
        private readonly string _label;
        private readonly Stopwatch _sw = Stopwatch.StartNew();
        private long _lastTicks;
        private double _lastPct = -1;
        private readonly int _updateMs;
        private bool _completed;
        private bool _printedAny;

        public ConsoleProgressScope(string label, int updateMs = 250)
        {
            _label = label;
            _updateMs = updateMs;
        }

        public void Report(long done, long total)
        {
            if (total <= 0) return;
            var now = _sw.ElapsedMilliseconds;
            if (now - _lastTicks < _updateMs && done < total) return;
            _lastTicks = now;
            double pct = Math.Min(100.0, done * 100.0 / total);
            if (pct - _lastPct < 0.2 && done < total) return;
            _lastPct = pct;

            double sec = Math.Max(0.001, _sw.Elapsed.TotalSeconds);
            double rate = done / sec; // bytes per second
            var eta = rate > 0 ? TimeSpan.FromSeconds((total - done) / rate) : TimeSpan.Zero;
            string line = $"{_label}: {pct,6:0.0}%  {FormatBytes(done)} / {FormatBytes(total)}  {FormatBytes((long)rate)}/s  ETA {FormatTime(eta)}";
            WriteUpdatingLine(line);
            _printedAny = true;
        }

        public void Complete()
        {
            if (_completed) return;
            _completed = true;
            if (_printedAny) Console.WriteLine();
        }

        private static void WriteUpdatingLine(string text)
        {
            // Overwrite the current console line
            int width = Console.WindowWidth > 0 ? Console.WindowWidth - 1 : 120;
            if (text.Length > width) text = text.Substring(0, width);
            Console.Write("\r" + text.PadRight(width));
        }

        internal static string FormatBytes(long b)
        {
            string[] units = { "B", "KiB", "MiB", "GiB", "TiB" };
            double v = b;
            int i = 0;
            while (v >= 1024 && i < units.Length - 1){ v /= 1024; i++; }
            return $"{v:0.##} {units[i]}";
        }

        internal static string FormatTime(TimeSpan t)
        {
            if (t.TotalHours >= 1) return $"{(int)t.TotalHours:D2}:{t.Minutes:D2}:{t.Seconds:D2}";
            return $"{t.Minutes:D2}:{t.Seconds:D2}";
        }

        public void Dispose() => Complete();
    }
}
