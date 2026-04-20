namespace DuplicatiIndexer.Data.Entities;

/// <summary>
/// Represents the indexing status of a file entry.
/// </summary>
public enum FileIndexingStatus
{
    /// <summary>
    /// The file has not been indexed yet.
    /// </summary>
    NotIndexed,

    /// <summary>
    /// The file is pending indexing.
    /// </summary>
    Pending,

    /// <summary>
    /// The file has been successfully indexed.
    /// </summary>
    Indexed,

    /// <summary>
    /// The file has no extractable content.
    /// </summary>
    NoContent,

    /// <summary>
    /// The file indexing failed.
    /// </summary>
    Failed
}

/// <summary>
/// Represents a file entry within a backup.
/// </summary>
public class BackupFileEntry
{
    /// <summary>
    /// Gets or sets the unique identifier for the file entry.
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// Gets or sets the identifier of the backup source this file belongs to.
    /// </summary>
    public Guid BackupSourceId { get; set; }

    /// <summary>
    /// Gets or sets the file path.
    /// </summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the file hash.
    /// </summary>
    public string Hash { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the file size in bytes.
    /// </summary>
    public long Size { get; set; }

    /// <summary>
    /// Gets or sets the last modified timestamp.
    /// </summary>
    public DateTime LastModified { get; set; }

    /// <summary>
    /// Gets or sets the version timestamp when this file was added.
    /// </summary>
    public DateTime VersionAdded { get; set; }

    /// <summary>
    /// Gets or sets the version timestamp when this file was deleted, if applicable.
    /// </summary>
    public DateTime? VersionDeleted { get; set; }

    /// <summary>
    /// Gets a value indicating whether the file is indexed.
    /// </summary>
    public bool IsIndexed => IndexingStatus == FileIndexingStatus.Indexed;

    /// <summary>
    /// Gets or sets the indexing status of the file.
    /// </summary>
    public FileIndexingStatus IndexingStatus { get; set; } = FileIndexingStatus.NotIndexed;

    /// <summary>
    /// Gets or sets the timestamp of the last indexing attempt.
    /// </summary>
    public DateTime? LastIndexingAttempt { get; set; }

    /// <summary>
    /// Gets or sets the error message if indexing failed.
    /// </summary>
    public string? IndexingErrorMessage { get; set; }
}
