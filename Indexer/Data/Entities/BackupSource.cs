namespace DuplicatiIndexer.Data.Entities;

/// <summary>
/// Represents a backup source configuration.
/// </summary>
public class BackupSource
{
    /// <summary>
    /// Gets or sets the unique identifier for the backup source.
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// Gets or sets the display name of the backup source.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the Duplicati backup identifier.
    /// </summary>
    public string DuplicatiBackupId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the timestamp when the backup source was created.
    /// </summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// Gets or sets the last parsed backup version timestamp.
    /// </summary>
    public DateTimeOffset? LastParsedVersion { get; set; }

    /// <summary>
    /// Gets or sets the encryption passphrase for the backup.
    /// Note: This should be stored encrypted in the database.
    /// </summary>
    public string? EncryptionPassword { get; set; }

    /// <summary>
    /// Gets or sets the target URL (connection string) for the backup storage.
    /// Note: This should be stored encrypted in the database.
    /// </summary>
    public string TargetUrl { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether the backup source processing is paused (e.g. circuit breaker tripped).
    /// </summary>
    public bool IsPaused { get; set; }

    /// <summary>
    /// Gets or sets the default group IDs for ACL on all indexed content from this backup.
    /// These are applied unless overridden by path-specific rules.
    /// </summary>
    public List<string> DefaultGroupIds { get; set; } = new();
}
