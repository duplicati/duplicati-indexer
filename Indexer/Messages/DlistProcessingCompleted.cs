namespace DuplicatiIndexer.Messages;

/// <summary>
/// Message indicating that a dlist file has been successfully processed.
/// This triggers the filename filter to check for new files that need indexing.
/// </summary>
public record DlistProcessingCompleted
{
    /// <summary>
    /// Gets the unique identifier for the backup (maps to BackupSource.DuplicatiBackupId).
    /// </summary>
    public required string BackupId { get; init; }

    /// <summary>
    /// Gets the backup source identifier.
    /// </summary>
    public required Guid BackupSourceId { get; init; }

    /// <summary>
    /// Gets the version timestamp that was processed (extracted from the dlist filename).
    /// </summary>
    public required DateTimeOffset Version { get; init; }

    /// <summary>
    /// Gets the number of new file entries added from this dlist.
    /// </summary>
    public required long NewFilesAdded { get; init; }

    /// <summary>
    /// Optional limit on the number of files to process natively bounding structural evaluation streams.
    /// </summary>
    public int? MaxFileCount { get; init; }
}
