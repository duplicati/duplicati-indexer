namespace DuplicatiIndexer.Data.Entities;

/// <summary>
/// Represents a file's presence in a specific backup version.
/// This tracks all files in each backup version, enabling queries like
/// "when was file xxx last modified?" and "which backup versions contain this file?"
/// </summary>
public class BackupVersionFile
{
    /// <summary>
    /// Gets or sets the unique identifier for this entry.
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// Gets or sets the identifier of the backup source.
    /// </summary>
    public Guid BackupSourceId { get; set; }

    /// <summary>
    /// Gets or sets the backup version timestamp.
    /// This represents when the backup was created.
    /// </summary>
    public DateTime Version { get; set; }

    /// <summary>
    /// Gets or sets the file path.
    /// </summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the file hash for this version.
    /// </summary>
    public string Hash { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the file size in bytes.
    /// </summary>
    public long Size { get; set; }

    /// <summary>
    /// Gets or sets the last modified timestamp of the original file.
    /// </summary>
    public DateTime LastModified { get; set; }

    /// <summary>
    /// Gets or sets the timestamp when this entry was recorded.
    /// </summary>
    public DateTime RecordedAt { get; set; }
}
