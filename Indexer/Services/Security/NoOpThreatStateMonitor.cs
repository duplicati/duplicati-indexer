using DuplicatiIndexer.Data.Entities;

namespace DuplicatiIndexer.Services.Security;

/// <summary>
/// No-op implementation of IThreatStateMonitor that does nothing.
/// Used when threat monitoring is disabled.
/// </summary>
public class NoOpThreatStateMonitor : IThreatStateMonitor
{
    public bool IsEnabled => false;

    public bool CheckForCanaryFiles(IEnumerable<string> filePaths)
    {
        return false;
    }

    public void RecordAnomalousFile(Guid backupSourceId)
    {
        // No-op
    }

    public bool IsVelocityThresholdExceeded(Guid backupSourceId)
    {
        return false;
    }
}
