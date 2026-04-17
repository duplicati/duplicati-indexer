namespace DuplicatiIndexer.Data.Entities;

/// <summary>
/// Represents indexed content for full-text search.
/// This entity is stored separately from BackupFileEntry to support
/// efficient full-text search operations using PostgreSQL's tsvector.
/// </summary>
public class IndexedContent
{
    /// <summary>
    /// Gets or sets the unique identifier (same as file chunk id or file id).
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// Gets or sets the file entry ID this content belongs to.
    /// </summary>
    public Guid FileEntryId { get; set; }

    /// <summary>
    /// Gets or sets the chunk index within the file, or -1 for non-chunked content.
    /// </summary>
    public int ChunkIndex { get; set; } = -1;

    /// <summary>
    /// Gets or sets a value indicating whether this is a chunk.
    /// </summary>
    public bool IsChunk => ChunkIndex >= 0;

    /// <summary>
    /// Gets or sets the original file path.
    /// </summary>
    public string FilePath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the text content.
    /// </summary>
    public string Content { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the timestamp when this content was indexed.
    /// </summary>
    public DateTimeOffset IndexedAt { get; set; }

    /// <summary>
    /// Gets or sets the list of group IDs that are allowed to access this content.
    /// </summary>
    public List<string> GroupIds { get; set; } = new();
}
