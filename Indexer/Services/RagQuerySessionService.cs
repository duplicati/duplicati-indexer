using DuplicatiIndexer.Data.Entities;
using DuplicatiIndexer.Data;

namespace DuplicatiIndexer.Services;

/// <summary>
/// Service for managing RAG query sessions and history.
/// Provides common session operations used by both V1 and V2 RAG query services.
/// </summary>
public class RagQuerySessionService
{
    private readonly ISurrealRepository _repository;
    private readonly ILogger<RagQuerySessionService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="RagQuerySessionService"/> class.
    /// </summary>
    /// <param name="repository">The Marten document session for database operations.</param>
    /// <param name="logger">The logger.</param>
    public RagQuerySessionService(
        ISurrealRepository repository,
        ILogger<RagQuerySessionService> logger)
    {
        _repository = repository;
        _logger = logger;
    }

    /// <summary>
    /// Gets or creates a query session.
    /// </summary>
    /// <param name="sessionId">The session identifier.</param>
    /// <param name="title">The title for new sessions.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A tuple containing the session and a boolean indicating if it's new.</returns>
    public async Task<(QuerySession Session, bool IsNew)> GetOrCreateSessionAsync(
        Guid sessionId,
        string title,
        CancellationToken cancellationToken = default)
    {
        var session = await _repository.GetAsync<QuerySession>(sessionId, cancellationToken);

        if (session != null)
        {
            // Update last activity timestamp
            session.LastActivityAt = DateTime.UtcNow.ToString("O");
            await _repository.StoreAsync(session, cancellationToken);
            return (session, false);
        }

        // Create new session
        var queryTimestamp = DateTime.UtcNow.ToString("O");
        session = new QuerySession
        {
            Id = sessionId,
            Title = title,
            CreatedAt = queryTimestamp,
            LastActivityAt = queryTimestamp
        };

        await _repository.StoreAsync(session, cancellationToken);
        _logger.LogInformation("Created new session {SessionId} with title: {Title}", sessionId, title);

        return (session, true);
    }

    /// <summary>
    /// Records a query and its response in the session history.
    /// </summary>
    /// <param name="sessionId">The session identifier.</param>
    /// <param name="originalQuery">The original query text.</param>
    /// <param name="condensedQuery">The condensed/standalone query.</param>
    /// <param name="response">The response/answer.</param>
    /// <param name="events">Optional list of events associated with the query.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The created history item.</returns>
    public async Task<QueryHistoryItem> RecordQueryAsync(
        Guid sessionId,
        string originalQuery,
        string condensedQuery,
        string response,
        List<QueryHistoryEvent>? events = null,
        CancellationToken cancellationToken = default)
    {
        var queryTimestamp = DateTime.UtcNow.ToString("O");
        var responseTimestamp = DateTime.UtcNow.ToString("O");

        var historyItem = new QueryHistoryItem
        {
            Id = Guid.NewGuid(),
            SessionId = sessionId,
            OriginalQuery = originalQuery,
            CondensedQuery = condensedQuery,
            Response = response,
            QueryTimestamp = queryTimestamp,
            ResponseTimestamp = responseTimestamp,
            Events = events ?? new List<QueryHistoryEvent>()
        };

        await _repository.StoreAsync(historyItem, cancellationToken);

        return historyItem;
    }

    /// <summary>
    /// Gets the query history for a session.
    /// </summary>
    /// <param name="sessionId">The session identifier.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The list of query history items for the session.</returns>
    public async Task<IReadOnlyList<QueryHistoryItem>> GetSessionHistoryAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default)
    {
        var sql = "SELECT * FROM queryhistoryitem WHERE SessionId = $sessionId ORDER BY QueryTimestamp ASC";
        var parameters = new Dictionary<string, object> { { "sessionId", sessionId } };
        return await _repository.QueryAsync<QueryHistoryItem>(sql, parameters, cancellationToken);
    }

    /// <summary>
    /// Gets a query session by ID.
    /// </summary>
    /// <param name="sessionId">The session identifier.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The query session, or null if not found.</returns>
    public async Task<QuerySession?> GetSessionAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default)
    {
        return await _repository.GetAsync<QuerySession>(sessionId, cancellationToken);
    }

    /// <summary>
    /// Gets the previous query history for a session.
    /// </summary>
    /// <param name="sessionId">The session identifier.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The list of query history items.</returns>
    public async Task<IReadOnlyList<QueryHistoryItem>> GetHistoryForSessionAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default)
    {
        var sql = "SELECT * FROM queryhistoryitem WHERE SessionId = $sessionId ORDER BY QueryTimestamp ASC";
        var parameters = new Dictionary<string, object> { { "sessionId", sessionId } };
        return await _repository.QueryAsync<QueryHistoryItem>(sql, parameters, cancellationToken);
    }

    /// <summary>
    /// Gets all query sessions ordered by most recent activity.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A list of all query sessions.</returns>
    public async Task<IReadOnlyList<QuerySession>> GetAllSessionsAsync(CancellationToken cancellationToken = default)
    {
        var sql = "SELECT * FROM querysession ORDER BY LastActivityAt DESC";
        return await _repository.QueryAsync<QuerySession>(sql, null, cancellationToken);
    }

    /// <summary>
    /// Reverts a session's history by deleting the target message and all subsequent messages.
    /// </summary>
    /// <param name="sessionId">The session identifier.</param>
    /// <param name="messageId">The message identifier to revert from.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    public async Task RevertSessionToMessageAsync(Guid sessionId, Guid messageId, CancellationToken cancellationToken = default)
    {
        var targetMessage = await _repository.GetAsync<QueryHistoryItem>(messageId, cancellationToken);
        if (targetMessage == null || targetMessage.SessionId != sessionId)
        {
            _logger.LogWarning("Revert requested for unknown or mismatched message {MessageId} in session {SessionId}", messageId, sessionId);
            return;
        }

        var sql = "DELETE queryhistoryitem WHERE SessionId = $sessionId AND QueryTimestamp >= $timestamp";
        var parameters = new Dictionary<string, object> 
        { 
            { "sessionId", sessionId }, 
            { "timestamp", targetMessage.QueryTimestamp }
        };

        // In SurrealDB, DELETE uses 'DELETE table WHERE'

        await _repository.QueryAsync<object>(sql, parameters, cancellationToken);
        _logger.LogInformation("Reverted session {SessionId} back to message {MessageId}", sessionId, messageId);
    }
}
