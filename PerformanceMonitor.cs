using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace ImagingUtility
{
    /// <summary>
    /// Monitors performance metrics and provides adaptive concurrency control
    /// </summary>
    internal class PerformanceMonitor
    {
        private readonly object _lock = new object();
        private readonly List<PerformanceSample> _samples = new List<PerformanceSample>();
        private readonly int _maxSamples = 20; // Keep last 20 samples
        private readonly TimeSpan _sampleInterval = TimeSpan.FromSeconds(2);
        private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
        
        private long _lastBytesProcessed = 0;
        private DateTime _lastSampleTime = DateTime.UtcNow;
        private int _currentParallel = 1;
        private int _currentPipelineDepth = 2;
        
        public int CurrentParallel => _currentParallel;
        public int CurrentPipelineDepth => _currentPipelineDepth;
        
        public struct PerformanceSample
        {
            public DateTime Timestamp;
            public long BytesProcessed;
            public double CpuUsage;
            public long MemoryUsage;
            public int ParallelWorkers;
            public int PipelineDepth;
            public double ThroughputMBps;
        }
        
        public PerformanceSample? GetLatestSample()
        {
            lock (_lock)
            {
                return _samples.Count > 0 ? _samples[_samples.Count - 1] : null;
            }
        }
        
        public void RecordProgress(long totalBytesProcessed)
        {
            var now = DateTime.UtcNow;
            var elapsed = now - _lastSampleTime;
            
            if (elapsed >= _sampleInterval)
            {
                var bytesDelta = totalBytesProcessed - _lastBytesProcessed;
                var throughputMBps = (bytesDelta / elapsed.TotalSeconds) / (1024.0 * 1024.0);
                
                var sample = new PerformanceSample
                {
                    Timestamp = now,
                    BytesProcessed = totalBytesProcessed,
                    CpuUsage = GetCpuUsage(),
                    MemoryUsage = GC.GetTotalMemory(false),
                    ParallelWorkers = _currentParallel,
                    PipelineDepth = _currentPipelineDepth,
                    ThroughputMBps = throughputMBps
                };
                
                lock (_lock)
                {
                    _samples.Add(sample);
                    if (_samples.Count > _maxSamples)
                        _samples.RemoveAt(0);
                }
                
                _lastBytesProcessed = totalBytesProcessed;
                _lastSampleTime = now;
            }
        }
        
        public void UpdateConcurrency(int parallel, int pipelineDepth)
        {
            _currentParallel = parallel;
            _currentPipelineDepth = pipelineDepth;
        }
        
        public (int recommendedParallel, int recommendedPipelineDepth) GetAdaptiveRecommendation()
        {
            lock (_lock)
            {
                if (_samples.Count < 3) // Need at least 3 samples for trend analysis
                    return (_currentParallel, _currentPipelineDepth);
                
                var recent = _samples.GetRange(Math.Max(0, _samples.Count - 5), Math.Min(5, _samples.Count));
                var avgThroughput = 0.0;
                var avgCpuUsage = 0.0;
                var avgMemoryUsage = 0.0;
                
                foreach (var sample in recent)
                {
                    avgThroughput += sample.ThroughputMBps;
                    avgCpuUsage += sample.CpuUsage;
                    avgMemoryUsage += sample.MemoryUsage;
                }
                
                avgThroughput /= recent.Count;
                avgCpuUsage /= recent.Count;
                avgMemoryUsage /= recent.Count;
                
                // Adaptive logic based on performance metrics
                int newParallel = _currentParallel;
                int newPipelineDepth = _currentPipelineDepth;
                
                // CPU-based adjustment
                if (avgCpuUsage < 50.0 && _currentParallel < Environment.ProcessorCount)
                {
                    newParallel = Math.Min(_currentParallel + 1, Environment.ProcessorCount);
                }
                else if (avgCpuUsage > 90.0 && _currentParallel > 1)
                {
                    newParallel = Math.Max(_currentParallel - 1, 1);
                }
                
                // Memory-based adjustment
                var availableMemory = GetAvailableMemory();
                if (avgMemoryUsage > availableMemory * 0.8 && _currentPipelineDepth > 1)
                {
                    newPipelineDepth = Math.Max(_currentPipelineDepth - 1, 1);
                }
                else if (avgMemoryUsage < availableMemory * 0.5 && _currentPipelineDepth < 4)
                {
                    newPipelineDepth = Math.Min(_currentPipelineDepth + 1, 4);
                }
                
                // Throughput-based adjustment
                if (_samples.Count >= 6)
                {
                    var older = _samples.GetRange(Math.Max(0, _samples.Count - 10), Math.Min(5, _samples.Count - 5));
                    var olderAvgThroughput = 0.0;
                    foreach (var sample in older) olderAvgThroughput += sample.ThroughputMBps;
                    olderAvgThroughput /= older.Count;
                    
                    if (avgThroughput < olderAvgThroughput * 0.9 && _currentParallel > 1)
                    {
                        // Throughput is declining, reduce parallelism
                        newParallel = Math.Max(_currentParallel - 1, 1);
                    }
                    else if (avgThroughput > olderAvgThroughput * 1.1 && _currentParallel < Environment.ProcessorCount)
                    {
                        // Throughput is improving, can try more parallelism
                        newParallel = Math.Min(_currentParallel + 1, Environment.ProcessorCount);
                    }
                }
                
                return (newParallel, newPipelineDepth);
            }
        }
        
        private double GetCpuUsage()
        {
            try
            {
                using var process = Process.GetCurrentProcess();
                return process.TotalProcessorTime.TotalMilliseconds / Environment.TickCount;
            }
            catch
            {
                return 0.0;
            }
        }
        
        private long GetAvailableMemory()
        {
            try
            {
                var memStatus = new Program.MEMORYSTATUSEX();
                if (Program.GlobalMemoryStatusEx(memStatus))
                {
                    return (long)memStatus.ullAvailPhys;
                }
            }
            catch { }
            return 8L * 1024 * 1024 * 1024; // Assume 8GB if we can't determine
        }
    }
    
    /// <summary>
    /// Enhanced parallel provider that includes adaptive concurrency control
    /// </summary>
    internal class AdaptiveParallelProvider
    {
        private readonly PerformanceMonitor _monitor;
        private readonly Func<int>? _externalProvider;
        private readonly CancellationTokenSource _cancellationTokenSource;
        private readonly Task _adaptiveTask;
        private volatile int _currentParallel;
        private volatile int _currentPipelineDepth;
        
        public AdaptiveParallelProvider(int initialParallel, int initialPipelineDepth, Func<int>? externalProvider = null)
        {
            _monitor = new PerformanceMonitor();
            _externalProvider = externalProvider;
            _currentParallel = initialParallel;
            _currentPipelineDepth = initialPipelineDepth;
            _cancellationTokenSource = new CancellationTokenSource();
            
            _monitor.UpdateConcurrency(initialParallel, initialPipelineDepth);
            
            // Start adaptive monitoring task
            _adaptiveTask = Task.Run(AdaptiveLoop);
        }
        
        public int GetParallel() => _currentParallel;
        public int GetPipelineDepth() => _currentPipelineDepth;
        
        public void RecordProgress(long bytesProcessed)
        {
            _monitor.RecordProgress(bytesProcessed);
        }
        
        private async Task AdaptiveLoop()
        {
            while (!_cancellationTokenSource.Token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(3), _cancellationTokenSource.Token);
                    
                    // Check external provider first
                    if (_externalProvider != null)
                    {
                        var externalParallel = _externalProvider();
                        if (externalParallel != _currentParallel)
                        {
                            _currentParallel = externalParallel;
                            _monitor.UpdateConcurrency(_currentParallel, _currentPipelineDepth);
                            continue;
                        }
                    }
                    
                    // Get adaptive recommendation
                    var (recommendedParallel, recommendedPipelineDepth) = _monitor.GetAdaptiveRecommendation();
                    
                    if (recommendedParallel != _currentParallel || recommendedPipelineDepth != _currentPipelineDepth)
                    {
                        _currentParallel = recommendedParallel;
                        _currentPipelineDepth = recommendedPipelineDepth;
                        _monitor.UpdateConcurrency(_currentParallel, _currentPipelineDepth);
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch
                {
                    // Continue on errors
                }
            }
        }
        
        public void Dispose()
        {
            _cancellationTokenSource.Cancel();
            try { _adaptiveTask.Wait(TimeSpan.FromSeconds(5)); } catch { }
            _cancellationTokenSource.Dispose();
        }
    }
}
