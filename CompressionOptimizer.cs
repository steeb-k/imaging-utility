using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using ZstdSharp;

namespace ImagingUtility
{
    /// <summary>
    /// Adaptive compression system that optimizes compression level based on data characteristics and system performance
    /// </summary>
    internal class CompressionOptimizer
    {
        private readonly List<CompressionSample> _samples = new List<CompressionSample>();
        private readonly int _maxSamples = 20;
        private int _currentLevel = 3; // Default zstd level
        private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
        
        public int CurrentLevel => _currentLevel;
        
        public struct CompressionSample
        {
            public DateTime Timestamp;
            public int CompressionLevel;
            public long InputSize;
            public long OutputSize;
            public double CompressionRatio;
            public double CompressionTimeMs;
            public double ThroughputMBps;
        }
        
        /// <summary>
        /// Records a compression sample for analysis
        /// </summary>
        public void RecordCompression(int level, long inputSize, long outputSize, double compressionTimeMs)
        {
            var sample = new CompressionSample
            {
                Timestamp = DateTime.UtcNow,
                CompressionLevel = level,
                InputSize = inputSize,
                OutputSize = outputSize,
                CompressionRatio = (double)outputSize / inputSize,
                CompressionTimeMs = compressionTimeMs,
                ThroughputMBps = (inputSize / (1024.0 * 1024.0)) / (compressionTimeMs / 1000.0)
            };
            
            lock (_samples)
            {
                _samples.Add(sample);
                if (_samples.Count > _maxSamples)
                {
                    _samples.RemoveAt(0);
                }
            }
        }
        
        /// <summary>
        /// Gets the optimal compression level based on recent performance
        /// </summary>
        public int GetOptimalCompressionLevel()
        {
            lock (_samples)
            {
                if (_samples.Count < 3) return _currentLevel;
                
                // Analyze recent samples
                var recent = _samples.TakeLast(Math.Min(10, _samples.Count)).ToList();
                
                // Calculate efficiency score for each level
                var levelScores = new Dictionary<int, double>();
                
                foreach (var sample in recent)
                {
                    if (!levelScores.ContainsKey(sample.CompressionLevel))
                    {
                        levelScores[sample.CompressionLevel] = 0;
                    }
                    
                    // Efficiency score: balance compression ratio and speed
                    // Higher ratio (better compression) and higher throughput (faster) = better score
                    double efficiencyScore = sample.CompressionRatio * sample.ThroughputMBps;
                    levelScores[sample.CompressionLevel] += efficiencyScore;
                }
                
                // Find the level with the best average efficiency
                int bestLevel = _currentLevel;
                double bestScore = 0;
                
                foreach (var kvp in levelScores)
                {
                    var levelSamples = recent.Where(s => s.CompressionLevel == kvp.Key).ToList();
                    if (levelSamples.Count > 0)
                    {
                        double avgScore = kvp.Value / levelSamples.Count;
                        if (avgScore > bestScore)
                        {
                            bestScore = avgScore;
                            bestLevel = kvp.Key;
                        }
                    }
                }
                
                // Gradual adjustment to avoid oscillation
                if (bestLevel != _currentLevel)
                {
                    int adjustment = bestLevel > _currentLevel ? 1 : -1;
                    _currentLevel = Math.Max(1, Math.Min(22, _currentLevel + adjustment));
                }
                
                return _currentLevel;
            }
        }
        
        /// <summary>
        /// Gets compression statistics for the current level
        /// </summary>
        public CompressionStats GetCompressionStats()
        {
            lock (_samples)
            {
                var recent = _samples.TakeLast(Math.Min(10, _samples.Count)).ToList();
                if (recent.Count == 0)
                {
                    return new CompressionStats();
                }
                
                return new CompressionStats
                {
                    AverageCompressionRatio = recent.Average(s => s.CompressionRatio),
                    AverageThroughputMBps = recent.Average(s => s.ThroughputMBps),
                    AverageCompressionTimeMs = recent.Average(s => s.CompressionTimeMs),
                    TotalSamples = recent.Count,
                    CurrentLevel = _currentLevel
                };
            }
        }
    }
    
    /// <summary>
    /// Compression statistics
    /// </summary>
    public struct CompressionStats
    {
        public double AverageCompressionRatio;
        public double AverageThroughputMBps;
        public double AverageCompressionTimeMs;
        public int TotalSamples;
        public int CurrentLevel;
        
        public double SpaceSavingsPercent => (1.0 - AverageCompressionRatio) * 100;
    }
    
    /// <summary>
    /// Enhanced compressor with adaptive level selection
    /// </summary>
    internal class AdaptiveCompressor : IDisposable
    {
        private readonly CompressionOptimizer _optimizer;
        private Compressor? _compressor;
        private int _lastLevel = -1;
        
        public AdaptiveCompressor(CompressionOptimizer optimizer)
        {
            _optimizer = optimizer;
        }
        
        public byte[] Compress(byte[] data)
        {
            var stopwatch = Stopwatch.StartNew();
            
            // Get optimal compression level
            int level = _optimizer.GetOptimalCompressionLevel();
            
            // Create new compressor if level changed
            if (level != _lastLevel)
            {
                _compressor?.Dispose();
                _compressor = new Compressor(level);
                _lastLevel = level;
            }
            
            // Compress data
            var compressed = _compressor!.Wrap(data).ToArray();
            
            stopwatch.Stop();
            
            // Record sample for optimization
            _optimizer.RecordCompression(level, data.Length, compressed.Length, stopwatch.Elapsed.TotalMilliseconds);
            
            return compressed;
        }
        
        public void Dispose()
        {
            _compressor?.Dispose();
        }
    }
    
    /// <summary>
    /// Compression level presets for different use cases
    /// </summary>
    internal static class CompressionPresets
    {
        public static readonly Dictionary<string, CompressionPreset> Presets = new()
        {
            ["fast"] = new CompressionPreset { Level = 1, Description = "Fast compression, lower ratio" },
            ["balanced"] = new CompressionPreset { Level = 3, Description = "Balanced speed and ratio" },
            ["high"] = new CompressionPreset { Level = 6, Description = "Higher compression ratio" },
            ["maximum"] = new CompressionPreset { Level = 22, Description = "Maximum compression ratio" },
            ["adaptive"] = new CompressionPreset { Level = -1, Description = "Adaptive based on data characteristics" }
        };
        
        public static CompressionPreset GetPreset(string name)
        {
            return Presets.TryGetValue(name.ToLowerInvariant(), out var preset) ? preset : Presets["balanced"];
        }
    }
    
    /// <summary>
    /// Compression preset configuration
    /// </summary>
    public struct CompressionPreset
    {
        public int Level;
        public string Description;
    }
}
