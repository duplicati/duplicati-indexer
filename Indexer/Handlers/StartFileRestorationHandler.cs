using DuplicatiIndexer.Data.Entities;
using DuplicatiIndexer.Messages;
using DuplicatiIndexer.Services;
using DuplicatiIndexer.Services.Security;
using Marten;
using Wolverine;
using Wolverine.Attributes;
using System.IO;

namespace DuplicatiIndexer.Handlers;

/// <summary>
/// Handler for processing StartFileRestoration messages.
/// Restores files to a temporary folder for indexing.
/// </summary>
public class StartFileRestorationHandler
{
    private readonly FileRestorer _fileRestorer;
    private readonly IDocumentSession _session;
    private readonly ILogger<StartFileRestorationHandler> _logger;
    private readonly IThreatStateMonitor _threatMonitor;

    /// <summary>
    /// Initializes a new instance of the <see cref="StartFileRestorationHandler"/> class.
    /// </summary>
    /// <param name="fileRestorer">The file restorer service.</param>
    /// <param name="session">The Marten document session.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="threatMonitor">The security threat monitor.</param>
    public StartFileRestorationHandler(
        FileRestorer fileRestorer,
        IDocumentSession session,
        ILogger<StartFileRestorationHandler> logger,
        IThreatStateMonitor threatMonitor)
    {
        _fileRestorer = fileRestorer;
        _session = session;
        _logger = logger;
        _threatMonitor = threatMonitor;
    }

    /// <summary>
    /// Handles a StartFileRestoration message by restoring files to a temporary folder.
    /// </summary>
    /// <param name="message">The StartFileRestoration message.</param>
    /// <param name="bus">The message bus for publishing follow-up messages.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    [WolverineHandler]
    public async Task Handle(StartFileRestoration message, IMessageBus bus, CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Received StartFileRestoration message for BackupId: {BackupId}, Version: {Version}, Files: {FileCount}",
            message.BackupId, message.Version, message.FilePaths.Count);

        try
        {
            // Get backup source configuration
            var backupSource = await _session.Query<BackupSource>()
                .FirstOrDefaultAsync(b => b.Id == message.BackupSourceId, cancellationToken);

            if (backupSource == null)
            {
                _logger.LogError(
                    "BackupSource not found for BackupSourceId: {BackupSourceId}",
                    message.BackupSourceId);
                throw new InvalidOperationException(
                    $"BackupSource not found for BackupSourceId: {message.BackupSourceId}");
            }

            if (string.IsNullOrEmpty(backupSource.TargetUrl))
            {
                _logger.LogError(
                    "BackupSource {BackupSourceId} does not have a TargetUrl configured.",
                    message.BackupSourceId);
                throw new InvalidOperationException(
                    $"BackupSource {message.BackupSourceId} does not have a TargetUrl configured.");
            }

            if (backupSource.IsPaused)
            {
                _logger.LogWarning("BackupSource {BackupSourceId} is currently PAUSED (potentially due to a security tripwire). Halting restoration processing.", message.BackupSourceId);
                return;
            }

            // Get file entries from database to restore
            var fileEntries = (await _session.Query<BackupFileEntry>()
                .Where(f => f.BackupSourceId == message.BackupSourceId
                         && f.VersionAdded == message.Version
                         && f.VersionDeleted == null
                         && message.FilePaths.Contains(f.Path))
                .ToListAsync(cancellationToken))
                .ToList();

            if (fileEntries.Count == 0)
            {
                _logger.LogWarning(
                    "No file entries found in database for restoration. BackupSourceId: {BackupSourceId}, Version: {Version}",
                    message.BackupSourceId, message.Version);
                return;
            }

            // --- Pre-flight Canary Verification ---
            if (_threatMonitor.CheckForCanaryFiles(fileEntries))
            {
                _logger.LogCritical("Canary tripwire detected for BackupSourceId: {BackupSourceId}. Pausing backup source.", message.BackupSourceId);
                backupSource.IsPaused = true;
                _session.Update(backupSource);
                await _session.SaveChangesAsync(cancellationToken);
                
                throw new InvalidOperationException($"Circuit breaker tripped: Canary file modified for BackupSource {message.BackupSourceId}. Backup paused.");
            }
            // --------------------------------------

            _logger.LogInformation(
                "Restoring {FileCount} files to {TargetDirectory} for BackupId: {BackupId}, Version: {Version}",
                fileEntries.Count, message.TargetDirectory, message.BackupId, message.Version);

            // Calculate the time parameter for Duplicati CLI
            // Duplicati uses --time to specify which backup version to restore from
            var timeParameter = CalculateTimeParameter(message.Version);

            // Restore files using FileRestorer
            var success = await _fileRestorer.RestoreFilesAsync(
                fileEntries,
                message.TargetDirectory,
                backupSource.TargetUrl,
                backupSource.EncryptionPassword,
                timeParameter);

            if (success)
            {
                _logger.LogInformation(
                    "Successfully restored {FileCount} files to {TargetDirectory} for BackupId: {BackupId}, Version: {Version}",
                    fileEntries.Count, message.TargetDirectory, message.BackupId, message.Version);

                // Publish ExtractTextAndIndex messages for each restored file
                foreach (var fileEntry in fileEntries)
                {
                    // Construct the restored file path
                    // The file is restored maintaining the directory structure within the target directory
                    var restoredFilePath = Path.Combine(message.TargetDirectory, fileEntry.Path.TrimStart('/', '\\'));

                    // --- Post-flight Ransomware Analyzer ---
                    if (_threatMonitor.IsEnabled && File.Exists(restoredFilePath))
                    {
                        var extension = Path.GetExtension(restoredFilePath).TrimStart('.');
                        if (RansomwareAnalyzer.IsHeaderlessOrTextFormat(extension))
                        {
                            var buffer = new byte[8192]; // Scan first 8KB
                            using var fs = new FileStream(restoredFilePath, FileMode.Open, FileAccess.Read);
                            int bytesRead = await fs.ReadAsync(buffer, cancellationToken);
                            
                            var span = new ReadOnlySpan<byte>(buffer, 0, bytesRead);
                            double entropy = RansomwareAnalyzer.CalculateEntropy(span);
                            
                            if (entropy > RansomwareAnalyzer.CRITICAL_ENTROPY_THRESHOLD)
                            {
                                _logger.LogWarning("Anomalous file detected! Entropy {Entropy} for file {FilePath}", entropy, fileEntry.Path);
                                _threatMonitor.RecordAnomalousFile(message.BackupSourceId);
                                
                                if (_threatMonitor.IsVelocityThresholdExceeded(message.BackupSourceId))
                                {
                                    _logger.LogCritical("Velocity threshold exceeded for BackupSourceId: {BackupSourceId}. Pausing backup.", message.BackupSourceId);
                                    backupSource.IsPaused = true;
                                    _session.Update(backupSource);
                                    await _session.SaveChangesAsync(cancellationToken);
                                    
                                    throw new InvalidOperationException($"Circuit breaker tripped: Velocity limit exceeded for BackupSource {message.BackupSourceId}.");
                                }
                                
                                File.Delete(restoredFilePath);
                                continue; // Skip indexing this junk
                            }
                        }
                        else 
                        {
                            var buffer = new byte[16];
                            using var fs = new FileStream(restoredFilePath, FileMode.Open, FileAccess.Read);
                            int bytesRead = await fs.ReadAsync(buffer, cancellationToken);
                            
                            var span = new ReadOnlySpan<byte>(buffer, 0, bytesRead);
                            if (!RansomwareAnalyzer.VerifyMagicBytes(span, extension))
                            {
                                _logger.LogWarning("Anomalous file detected! Magic bytes mismatch for file {FilePath}", fileEntry.Path);
                                _threatMonitor.RecordAnomalousFile(message.BackupSourceId);
                                
                                if (_threatMonitor.IsVelocityThresholdExceeded(message.BackupSourceId))
                                {
                                    _logger.LogCritical("Velocity threshold exceeded for BackupSourceId: {BackupSourceId}. Pausing backup.", message.BackupSourceId);
                                    backupSource.IsPaused = true;
                                    _session.Update(backupSource);
                                    await _session.SaveChangesAsync(cancellationToken);
                                    
                                    throw new InvalidOperationException($"Circuit breaker tripped: Velocity limit exceeded for BackupSource {message.BackupSourceId}.");
                                }
                                
                                File.Delete(restoredFilePath);
                                continue;
                            }
                        }
                    }
                    // ---------------------------------------

                    _logger.LogInformation(
                        "Publishing ExtractTextAndIndex message for FileEntryId: {FileEntryId}, Path: {OriginalPath}",
                        fileEntry.Id, fileEntry.Path);

                    await bus.PublishAsync(new ExtractTextAndIndex
                    {
                        BackupId = message.BackupId,
                        BackupSourceId = message.BackupSourceId,
                        Version = message.Version,
                        FileEntryId = fileEntry.Id,
                        RestoredFilePath = restoredFilePath,
                        OriginalFilePath = fileEntry.Path,
                        DefaultGroupIds = backupSource.DefaultGroupIds.AsReadOnly()
                    });
                }

                _logger.LogInformation(
                    "Published {FileCount} ExtractTextAndIndex messages for BackupId: {BackupId}, Version: {Version}",
                    fileEntries.Count, message.BackupId, message.Version);
            }
            else
            {
                _logger.LogError(
                    "Failed to restore files for BackupId: {BackupId}, Version: {Version}",
                    message.BackupId, message.Version);
                throw new InvalidOperationException(
                    $"File restoration failed for BackupId: {message.BackupId}, Version: {message.Version}");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Error processing StartFileRestoration message for BackupId: {BackupId}, Version: {Version}",
                message.BackupId, message.Version);
            throw;
        }
    }

    /// <summary>
    /// Calculates the time parameter for Duplicati CLI based on the backup version timestamp.
    /// </summary>
    /// <param name="version">The version timestamp.</param>
    /// <returns>The time parameter string to use with Duplicati CLI --time option.</returns>
    private static string CalculateTimeParameter(DateTimeOffset version)
    {
        // Duplicati's --time parameter accepts ISO 8601 format
        // Format as "2026-03-16T10:30:00" which Duplicati can parse
        return version.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ");
    }
}
