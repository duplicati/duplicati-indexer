namespace DuplicatiIndexer.Messages;

/// <summary>
/// Message indicating a new backup version has been created.
/// The version timestamp is extracted from the dlist filename (format: duplicati-YYYYMMDDTHHMMSSZ.dlist.zip[.aes]).
/// </summary>
public record BackupVersionCreated
{
    /// <summary>
    /// Gets the unique identifier for the backup (maps to BackupSource.DuplicatiBackupId).
    /// </summary>
    public required string BackupId { get; init; }

    /// <summary>
    /// Gets the dlist filename (e.g., "duplicati-20240312T143000Z.dlist.zip.aes").
    /// The version timestamp is extracted from this filename.
    /// </summary>
    public required string DlistFilename { get; init; }

    /// <summary>
    /// Optional limit on the number of files to process from this backup version.
    /// Mostly used for testing or partial ingestions.
    /// </summary>
    public int? MaxFileCount { get; init; }
}
