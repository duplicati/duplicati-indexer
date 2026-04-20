using System.Collections.Concurrent;
using DuplicatiIndexer.Data.Entities;

namespace DuplicatiIndexer.Services.Security;

public class ThreatStateMonitor : IThreatStateMonitor
{
    private readonly ILogger<ThreatStateMonitor> _logger;
    private readonly ConcurrentDictionary<Guid, VelocityBucket> _velocityCounters = new();

    public bool IsEnabled => true;

    // Threshold triggering a circuit breaker if N anomalous files are detected in 1 minute.
    private const int MAX_ANOMALOUS_FILES_PER_MINUTE = 10;

    public ThreatStateMonitor(ILogger<ThreatStateMonitor> logger)
    {
        _logger = logger;
    }

    public bool CheckForCanaryFiles(IEnumerable<string> filePaths)
    {
        foreach (var path in filePaths)
        {
            var pathSpan = path.AsSpan();
            if (pathSpan.Contains("_canary", StringComparison.OrdinalIgnoreCase) ||
                pathSpan.Contains(".tripwire", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogCritical("Canary tripwire triggered by maliciously modified file: {FilePath}", path);
                return true;
            }
        }

        return false;
    }

    public void RecordAnomalousFile(Guid backupSourceId)
    {
        var bucket = _velocityCounters.GetOrAdd(backupSourceId, _ => new VelocityBucket());
        bucket.AddStrike();
        _logger.LogWarning("Recorded anomalous file for BackupSourceId: {BackupSourceId}. Strike Count in current window: {Count}", backupSourceId, bucket.CurrentStrikes);
    }

    public bool IsVelocityThresholdExceeded(Guid backupSourceId)
    {
        if (_velocityCounters.TryGetValue(backupSourceId, out var bucket))
        {
            return bucket.CurrentStrikes >= MAX_ANOMALOUS_FILES_PER_MINUTE;
        }
        return false;
    }

    private sealed class VelocityBucket
    {
        private int _strikes;
        private DateTimeOffset _windowStart = DateTimeOffset.UtcNow;
        private readonly object _lock = new object();

        public int CurrentStrikes => _strikes;

        public void AddStrike()
        {
            lock (_lock)
            {
                var now = DateTimeOffset.UtcNow;
                if ((now - _windowStart).TotalMinutes > 1)
                {
                    // Reset window
                    _windowStart = now;
                    _strikes = 1;
                }
                else
                {
                    _strikes++;
                }
            }
        }
    }
}
