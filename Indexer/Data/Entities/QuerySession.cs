namespace DuplicatiIndexer.Data.Entities;

/// <summary>
/// Represents a query session for storing conversation context.
/// </summary>
public class QuerySession
{
    /// <summary>
    /// Gets or sets the unique identifier for the session.
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// Gets or sets the display title for the session.
    /// </summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the timestamp when the session was created.
    /// </summary>
    public string CreatedAt { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the timestamp of the last activity in the session.
    /// </summary>
    public string LastActivityAt { get; set; } = string.Empty;
}
