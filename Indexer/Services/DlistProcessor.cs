using System.Diagnostics;
using Duplicati.Library.Compression.ZipCompression;
using Duplicati.Library.Encryption;
using Duplicati.Library.Interface;
using Duplicati.Library.Main;
using Duplicati.Library.Main.Volumes;
using DuplicatiIndexer.Data.Entities;
using DuplicatiIndexer.Data;
using Microsoft.Extensions.Logging;
using SharpCompress.Archives.Zip;
using Wolverine;

namespace DuplicatiIndexer.Services;

/// <summary>
/// Result of processing a dlist file.
/// </summary>
public class CountResult {
    public int count { get; set; }
}

public class DlistProcessingResult
{
    /// <summary>
    /// Gets or sets a value indicating whether processing was successful.
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// Gets or sets the backup identifier.
    /// </summary>
    public string BackupId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the backup source identifier.
    /// </summary>
    public Guid BackupSourceId { get; set; }

    /// <summary>
    /// Gets or sets the version timestamp.
    /// </summary>
    public DateTimeOffset Version { get; set; }

    /// <summary>
    /// Gets or sets the number of new file entries added.
    /// </summary>
    public long NewFilesAdded { get; set; }

    /// <summary>
    /// Gets or sets the error message if processing failed.
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// Optional limit on the number of files to process organically bypassing evaluation structures.
    /// </summary>
    public int? MaxFileCount { get; set; }
}

/// <summary>
/// Processes Duplicati dlist files to extract file metadata.
/// </summary>
public class DlistProcessor
{
    private static Guid CreateDeterministicGuid(string input)
    {
        using (var md5 = System.Security.Cryptography.MD5.Create())
        {
            var hash = md5.ComputeHash(System.Text.Encoding.UTF8.GetBytes(input));
            return new Guid(hash);
        }
    }
    private readonly ISurrealRepository _repository;
    private readonly IMessageBus _bus;
    private readonly ILogger<DlistProcessor> _logger;
    private readonly DbStatsLiveMonitor _statsLiveMonitor;

    /// <summary>
    /// Initializes a new instance of the <see cref="DlistProcessor"/> class.
    /// </summary>
    /// <param name="repository">The surreal repository.</param>
    /// <param name="bus">The message bus.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="statsLiveMonitor">The live stats monitor.</param>
    public DlistProcessor(
        ISurrealRepository repository,
        IMessageBus bus,
        ILogger<DlistProcessor> logger,
        DbStatsLiveMonitor statsLiveMonitor)
    {
        _repository = repository;
        _bus = bus;
        _logger = logger;
        _statsLiveMonitor = statsLiveMonitor;
    }

    /// <summary>
    /// Processes a dlist file and stores file entries in the database.
    /// </summary>
    /// <param name="backupId">The backup identifier.</param>
    /// <param name="version">The backup version timestamp extracted from the dlist filename.</param>
    /// <param name="dlistFilePath">The path to the dlist file.</param>
    /// <param name="passphrase">The encryption passphrase, if the file is encrypted.</param>
    /// <param name="maxFileCount">Optional maximum number of files to process.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A result containing information about the processing operation.</returns>
    public async Task<DlistProcessingResult> ProcessDlistAsync(string backupId, DateTimeOffset version, string dlistFilePath, string? passphrase, int? maxFileCount = null, CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        _logger.LogInformation("Processing dlist file {DlistFilePath} for backup {BackupId} version {Version}", dlistFilePath, backupId, version);

        if (!File.Exists(dlistFilePath))
        {
            _logger.LogError("Dlist file not found: {DlistFilePath}", dlistFilePath);
            return new DlistProcessingResult
            {
                Success = false,
                BackupId = backupId,
                Version = version,
                ErrorMessage = $"Dlist file not found: {dlistFilePath}"
            };
        }

        string? tempDecryptedFile = null;
        string fileToProcess = dlistFilePath;
        Guid backupSourceId = Guid.Empty;
        long totalEntries = 0;

        try
        {
            if (dlistFilePath.EndsWith(".aes", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrEmpty(passphrase))
                {
                    _logger.LogError("Passphrase required to decrypt {DlistFilePath}", dlistFilePath);
                    return new DlistProcessingResult
                    {
                        Success = false,
                        BackupId = backupId,
                        Version = version,
                        ErrorMessage = $"Passphrase required to decrypt {dlistFilePath}"
                    };
                }

                tempDecryptedFile = Path.GetTempFileName();
                _logger.LogInformation("Decrypting {DlistFilePath} to {TempFile}", dlistFilePath, tempDecryptedFile);

                using var aes = new AESEncryption(passphrase, new Dictionary<string, string>());
                aes.Decrypt(dlistFilePath, tempDecryptedFile);
                fileToProcess = tempDecryptedFile;
            }

            var options = new Options(new Dictionary<string, string?>());

            using var fileStream = new FileStream(fileToProcess, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var zip = new FileArchiveZip(fileStream, ArchiveMode.Read, new Dictionary<string, string?>());
            using var reader = new FilesetVolumeReader(zip, options);

            var backupSource = await _repository.QueryScalarAsync<BackupSource>(
                "SELECT * FROM backupsource WHERE DuplicatiBackupId = $id",
                new Dictionary<string, object> { { "id", backupId } }, cancellationToken);
            if (backupSource == null)
            {
                throw new InvalidOperationException(
                    $"BackupSource not found for BackupId: {backupId}. " +
                    "Please ensure the backup source is configured with encryption password and target URL.");
            }

            backupSourceId = backupSource.Id;

            const int batchSize = 5000;
            var fileEntryBatch = new List<BackupFileEntry>();
            var versionFileBatch = new List<BackupVersionFile>();
            long totalEntriesLong = 0;

            // Natively count idempotently persisted items for immediate cursor pagination
            var existingCountResult = await _repository.QueryAsync<CountResult>(
                "SELECT count() FROM backupfileentry WHERE BackupSourceId = $id GROUP ALL",
                new Dictionary<string, object> { { "id", backupSourceId } },
                cancellationToken);
            
            var existingCount = existingCountResult.FirstOrDefault()?.count ?? 0;
            
            if (existingCount > 0)
            {
                _logger.LogInformation("Cursor pagination active: Skipping {ExistingCount} previously checkpointed files out of the data stream...", existingCount);
                _statsLiveMonitor.InitializeBaselineOnce(existingCount, existingCount);
            }

            // Collect all file keys from the dlist stream and aggressively skip to our cursor
            var dlistFilesQuery = reader.Files
                .Where(f => f.Type == FilelistEntryType.File)
                .Skip(existingCount)
                .Select(f => new { f.Path, f.Hash, f.Size, f.Time });

            if (maxFileCount.HasValue)
            {
                // Take only the remaining files to satisfy the limit relative to total structure
                var remaining = maxFileCount.Value - existingCount;
                if (remaining < 0) remaining = 0;
                dlistFilesQuery = dlistFilesQuery.Take(remaining);
            }

            var dlistFiles = dlistFilesQuery.ToList();

            var localSeenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var localSeenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (dlistFiles.Count == 0 && existingCount > 0)
            {
                _logger.LogInformation("Cursor perfectly aligns with stream boundary. Entire Dlist was already processed natively.");
            }
            else
            {
                _logger.LogInformation("Synthesizing deterministic structural IDs and mathematically upserting signatures to SurrealDB index...");
            }

            foreach (var file in dlistFiles)
            {
                if (localSeenPaths.Add(file.Path))
                {
                    versionFileBatch.Add(new BackupVersionFile
                    {
                        Id = CreateDeterministicGuid($"{backupSource.Id}|{version.UtcDateTime:O}|{file.Path}"),
                        BackupSourceId = backupSource.Id,
                        Version = version.UtcDateTime,
                        Path = file.Path,
                        Hash = file.Hash,
                        Size = file.Size,
                        LastModified = file.Time.ToUniversalTime(),
                        RecordedAt = DateTime.UtcNow
                    });
                }

                var fileKey = $"{file.Path}|{file.Hash}";
                if (localSeenKeys.Add(fileKey))
                {
                    fileEntryBatch.Add(new BackupFileEntry
                    {
                        Id = CreateDeterministicGuid($"{backupSource.Id}|{file.Path}|{file.Hash}"),
                        BackupSourceId = backupSource.Id,
                        VersionAdded = version.UtcDateTime,
                        Path = file.Path,
                        Hash = file.Hash,
                        Size = file.Size,
                        LastModified = file.Time.ToUniversalTime()
                    });
                }

                // Save batches natively utilizing internal DB layer pipeline chunking
                if (fileEntryBatch.Count >= batchSize)
                {
                    await _repository.StoreManyAsync(fileEntryBatch, cancellationToken);
                    _statsLiveMonitor.IncrementMetadata(fileEntryBatch.Count);
                    totalEntriesLong += fileEntryBatch.Count;
                    fileEntryBatch.Clear();
                }

                if (versionFileBatch.Count >= batchSize)
                {
                    await _repository.StoreManyAsync(versionFileBatch, cancellationToken);
                    _statsLiveMonitor.IncrementVersionFile(versionFileBatch.Count);
                    versionFileBatch.Clear();
                }
            }

            // Insert remaining entries
            if (fileEntryBatch.Count > 0)
            {
                await _repository.StoreManyAsync(fileEntryBatch, cancellationToken);
                _statsLiveMonitor.IncrementMetadata(fileEntryBatch.Count);
                totalEntriesLong += fileEntryBatch.Count;
            }

            if (versionFileBatch.Count > 0)
            {
                await _repository.StoreManyAsync(versionFileBatch, cancellationToken);
                _statsLiveMonitor.IncrementVersionFile(versionFileBatch.Count);
            }

            totalEntries = (int)totalEntriesLong + existingCount;
            _logger.LogInformation("Successfully completed deterministic metadata UPSERT block! Total elements evaluated natively: {Count}", totalEntries);


            _logger.LogInformation("Found {Count} file entries in {DlistFilePath}", totalEntries, dlistFilePath);

            // Update BackupSource LastParsedVersion
            if (backupSource.LastParsedVersion == null || version > backupSource.LastParsedVersion)
            {
                backupSource.LastParsedVersion = version;
            }

            await _repository.StoreAsync(backupSource, cancellationToken);
            _logger.LogInformation("Successfully processed dlist file {DlistFilePath}", dlistFilePath);

            return new DlistProcessingResult
            {
                Success = true,
                BackupId = backupId,
                BackupSourceId = backupSourceId,
                Version = version,
                NewFilesAdded = totalEntries,
                MaxFileCount = maxFileCount
            };
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _logger.LogError(ex, "Error processing dlist file {DlistFilePath} after {ElapsedMs}ms", dlistFilePath, stopwatch.ElapsedMilliseconds);
            throw;
        }
        finally
        {
            if (tempDecryptedFile != null && File.Exists(tempDecryptedFile))
            {
                try
                {
                    File.Delete(tempDecryptedFile);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to delete temporary decrypted file {TempFile}", tempDecryptedFile);
                }
            }
        }
    }
}
