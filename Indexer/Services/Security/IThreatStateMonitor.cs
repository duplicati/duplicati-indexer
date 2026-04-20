using DuplicatiIndexer.Data.Entities;

namespace DuplicatiIndexer.Services.Security;

/// <summary>
/// Monitors circuit breaker conditions for Ransomware protection.
/// </summary>
public interface IThreatStateMonitor
{
    /// <summary>
    /// Gets a value indicating whether anomaly detection is enabled.
    /// When false, anomaly detection should be skipped entirely.
    /// </summary>
    bool IsEnabled { get; }

    /// <summary>
    /// Scans a batch of modified files for canary tripwires.
    /// </summary>
    bool CheckForCanaryFiles(IEnumerable<string> filePaths);

    /// <summary>
    /// Records an anomalous file event for a given backup source.
    /// </summary>
    void RecordAnomalousFile(Guid backupSourceId);

    /// <summary>
    /// Checks if a backup source has exceeded the velocity threshold map.
    /// </summary>
    bool IsVelocityThresholdExceeded(Guid backupSourceId);
}
