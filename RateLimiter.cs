using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace ImagingUtility
{
    /// <summary>
    /// Rate limiter for controlling throughput and smoothing I/O operations
    /// </summary>
    internal class RateLimiter
    {
        private readonly long _maxBytesPerSecond;
        private readonly Stopwatch _stopwatch;
        private readonly object _lock = new object();
        private long _bytesProcessed = 0;
        private DateTime _lastResetTime;
        private readonly bool _enableSmoothing;
        private readonly int _smoothingWindowMs;
        
        public RateLimiter(long maxBytesPerSecond = 0, bool enableSmoothing = false, int smoothingWindowMs = 1000)
        {
            _maxBytesPerSecond = maxBytesPerSecond;
            _enableSmoothing = enableSmoothing;
            _smoothingWindowMs = smoothingWindowMs;
            _stopwatch = Stopwatch.StartNew();
            _lastResetTime = DateTime.UtcNow;
        }
        
        public long MaxBytesPerSecond => _maxBytesPerSecond;
        public bool IsEnabled => _maxBytesPerSecond > 0;
        
        /// <summary>
        /// Records bytes processed and returns delay needed to maintain rate limit
        /// </summary>
        public async Task RecordBytesAsync(long bytes)
        {
            if (!IsEnabled) return;
            
            bool needsDelay = false;
            int delayMs = 0;
            
            lock (_lock)
            {
                _bytesProcessed += bytes;
                
                var now = DateTime.UtcNow;
                var elapsed = now - _lastResetTime;
                
                // Reset counters every smoothing window
                if (elapsed.TotalMilliseconds >= _smoothingWindowMs)
                {
                    _bytesProcessed = bytes;
                    _lastResetTime = now;
                    return;
                }
                
                // Calculate if we need to throttle
                var targetElapsed = TimeSpan.FromSeconds((double)_bytesProcessed / _maxBytesPerSecond);
                var actualElapsed = elapsed;
                
                if (actualElapsed < targetElapsed)
                {
                    delayMs = (int)(targetElapsed - actualElapsed).TotalMilliseconds;
                    if (delayMs > 0)
                    {
                        needsDelay = true;
                    }
                }
            }
            
            if (needsDelay)
            {
                await Task.Delay(delayMs);
            }
        }
        
        /// <summary>
        /// Gets current throughput in bytes per second
        /// </summary>
        public double GetCurrentThroughput()
        {
            lock (_lock)
            {
                var elapsed = DateTime.UtcNow - _lastResetTime;
                if (elapsed.TotalSeconds <= 0) return 0;
                return _bytesProcessed / elapsed.TotalSeconds;
            }
        }
        
        /// <summary>
        /// Gets the percentage of rate limit currently being used
        /// </summary>
        public double GetRateLimitUsage()
        {
            if (!IsEnabled) return 0;
            
            lock (_lock)
            {
                var currentThroughput = GetCurrentThroughput();
                return Math.Min(1.0, currentThroughput / _maxBytesPerSecond);
            }
        }
    }
    
    /// <summary>
    /// Adaptive rate limiter that adjusts based on system performance
    /// </summary>
    internal class AdaptiveRateLimiter : RateLimiter
    {
        private readonly PerformanceMonitor _monitor;
        private readonly Timer _adjustmentTimer;
        private long _currentMaxBytesPerSecond;
        private readonly long _minBytesPerSecond;
        private readonly long _maxBytesPerSecond;
        private readonly double _cpuThreshold;
        private readonly double _memoryThreshold;
        
        public AdaptiveRateLimiter(long initialMaxBytesPerSecond, long minBytesPerSecond, long maxBytesPerSecond, 
            double cpuThreshold = 80.0, double memoryThreshold = 85.0) 
            : base(initialMaxBytesPerSecond, enableSmoothing: true)
        {
            _currentMaxBytesPerSecond = initialMaxBytesPerSecond;
            _minBytesPerSecond = minBytesPerSecond;
            _maxBytesPerSecond = maxBytesPerSecond;
            _cpuThreshold = cpuThreshold;
            _memoryThreshold = memoryThreshold;
            _monitor = new PerformanceMonitor();
            
            // Adjust rate every 5 seconds
            _adjustmentTimer = new Timer(AdjustRate, null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
        }
        
        private void AdjustRate(object? state)
        {
            try
            {
                var sample = _monitor.GetLatestSample();
                if (sample == null) return;
                
                var newRate = _currentMaxBytesPerSecond;
                
                // CPU-based adjustment
                if (sample.Value.CpuUsage > _cpuThreshold)
                {
                    newRate = Math.Max(_minBytesPerSecond, (long)(_currentMaxBytesPerSecond * 0.8));
                }
                else if (sample.Value.CpuUsage < _cpuThreshold * 0.5)
                {
                    newRate = Math.Min(_maxBytesPerSecond, (long)(_currentMaxBytesPerSecond * 1.1));
                }
                
                // Memory-based adjustment
                var availableMemory = GetAvailableMemory();
                var memoryUsagePercent = (double)sample.Value.MemoryUsage / availableMemory * 100;
                
                if (memoryUsagePercent > _memoryThreshold)
                {
                    newRate = Math.Max(_minBytesPerSecond, (long)(newRate * 0.7));
                }
                else if (memoryUsagePercent < _memoryThreshold * 0.6)
                {
                    newRate = Math.Min(_maxBytesPerSecond, (long)(newRate * 1.05));
                }
                
                // Throughput-based adjustment
                if (sample.Value.ThroughputMBps > 0)
                {
                    var currentThroughputBps = sample.Value.ThroughputMBps * 1024 * 1024;
                    if (currentThroughputBps < _currentMaxBytesPerSecond * 0.5)
                    {
                        // Throughput is much lower than rate limit, increase it
                        newRate = Math.Min(_maxBytesPerSecond, (long)(newRate * 1.2));
                    }
                }
                
                if (newRate != _currentMaxBytesPerSecond)
                {
                    _currentMaxBytesPerSecond = newRate;
                    // Update the base rate limiter
                    // Note: This would require modifying the base class to support dynamic rate changes
                }
            }
            catch
            {
                // Continue on errors
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
        
        public void RecordProgress(long bytesProcessed)
        {
            _monitor.RecordProgress(bytesProcessed);
        }
        
        public void Dispose()
        {
            _adjustmentTimer?.Dispose();
        }
    }
    
    /// <summary>
    /// Extension methods for rate limiting integration
    /// </summary>
    internal static class RateLimiterExtensions
    {
        public static async Task RecordBytesWithRateLimitAsync(this RateLimiter? rateLimiter, long bytes)
        {
            if (rateLimiter != null)
            {
                await rateLimiter.RecordBytesAsync(bytes);
            }
        }
    }
}
