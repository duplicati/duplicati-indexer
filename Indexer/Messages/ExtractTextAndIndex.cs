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
    /// Gets the unique identifier of the file entry.
    /// </summary>
    public required Guid FileEntryId { get; init; }

    /// <summary>
    /// Gets the full path to the restored file on disk.
    /// </summary>
    public required string RestoredFilePath { get; init; }

    /// <summary>
    /// Gets the original file path in the backup.
    /// </summary>
    public required string OriginalFilePath { get; init; }

    /// <summary>
    /// Gets the default group IDs for ACL on this file's content.
    /// These are the backup source's default groups.
    /// </summary>
    public IReadOnlyList<string> DefaultGroupIds { get; init; } = new List<string>();
}
