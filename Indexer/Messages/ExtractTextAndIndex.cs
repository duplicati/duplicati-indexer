namespace DuplicatiIndexer.Messages;

/// <summary>
/// Message instructing the system to extract text from a restored file and index it into the vector database.
/// </summary>
public record ExtractTextAndIndex
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
    /// Gets the version timestamp of the backup.
    /// </summary>
    public required DateTimeOffset Version { get; init; }

    /// <summary>
    /// Gets the full path to the restored file on disk.
    /// </summary>
    public required string RestoredFilePath { get; init; }

    /// <summary>
    /// Gets the original file path in the backup.
    /// </summary>
    public required string OriginalFilePath { get; init; }

    /// <summary>
    /// Gets the file entry identifier for direct O(1) database lookup.
    /// </summary>
    public Guid? FileEntryId { get; init; }
}
