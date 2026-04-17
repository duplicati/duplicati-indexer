using DuplicatiIndexer.AdapterInterfaces;
using DuplicatiIndexer.Data.Entities;

namespace DuplicatiIndexer.Services;

/// <summary>
/// Service for performing RAG (Retrieval-Augmented Generation) queries with session management.
/// Traditional RAG approach with automatic query embedding and vector search.
/// </summary>
public class DirectQueryService : IRagQueryService
{
    private readonly ILLMQueryService _llmService;
    private readonly IHybridSearchService _hybridSearchService;
    private readonly RagQuerySessionService _sessionService;
    private readonly IGroupPermissionService _groupPermissionService;
    private readonly ILogger<DirectQueryService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="DirectQueryService"/> class.
    /// </summary>
    /// <param name="llmService">The LLM service for generating answers.</param>
    /// <param name="hybridSearchService">The hybrid search service for searching content.</param>
    /// <param name="sessionService">The session service for managing query sessions.</param>
    /// <param name="groupPermissionService">The group permission service for ACL.</param>
    /// <param name="logger">The logger.</param>
    public DirectQueryService(
        ILLMQueryService llmService,
        IHybridSearchService hybridSearchService,
        RagQuerySessionService sessionService,
        IGroupPermissionService groupPermissionService,
        ILogger<DirectQueryService> logger)
    {
        _llmService = llmService;
        _hybridSearchService = hybridSearchService;
        _sessionService = sessionService;
        _groupPermissionService = groupPermissionService;
        _logger = logger;
    }

    /// <summary>
    /// Executes a RAG query to find and answer questions based on indexed content.
    /// Manages session state including query history and title generation.
    /// </summary>
    /// <param name="sessionId">The session identifier.</param>
    /// <param name="query">The user's question.</param>
    /// <param name="topK">The number of similar documents to retrieve.</param>
    /// <param name="onEvent">Optional callback for stream progress events.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The query result containing the answer and session information.</returns>
    public async Task<RagQueryResult> QueryAsync(Guid sessionId, string query, int topK = 5, Func<RagQueryEvent, Task>? onEvent = null, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Processing RAG query for session {SessionId}: {Query}", sessionId, query);

        // Get the current user's allowed group IDs for ACL filtering
        var allowedGroupIds = _groupPermissionService.GetCurrentUserGroupIds();
        _logger.LogInformation("Querying with allowed groups: {Groups}", string.Join(", ", allowedGroupIds));

        // 1. Get or create session
        var title = await _llmService.GenerateSessionTitleAsync(query, cancellationToken);
        var (session, isNewSession) = await _sessionService.GetOrCreateSessionAsync(sessionId, title, cancellationToken);

        // 2. Get previous query history for this session
        var history = await _sessionService.GetHistoryForSessionAsync(sessionId, cancellationToken);

        // 3. Condense the query if there's history, otherwise use the original query
        string condensedQuery;
        if (history.Any())
        {
            var conversationHistory = history.Select(h => new ConversationHistoryItem
            {
                Query = h.OriginalQuery,
                Response = h.Response
            });

            condensedQuery = await _llmService.CondenseQueryAsync(conversationHistory, query, cancellationToken);
            _logger.LogInformation("Condensed query for session {SessionId}: {CondensedQuery}", sessionId, condensedQuery);
        }
        else
        {
            condensedQuery = query;
        }

        // 4. Perform hybrid search using both vector and full-text search with ACL filtering
        var searchOptions = new HybridSearchOptions
        {
            TopKPerMethod = topK * 2,  // Get more from each method for better fusion
            FinalTopK = topK,
            RrfK = 60
        };

        var searchResults = await _hybridSearchService.SearchAsync(condensedQuery, allowedGroupIds, searchOptions, cancellationToken);

        // 5. Combine retrieved content into context
        var context = string.Join("\n\n---\n\n", searchResults.Select(r => r.Content));

        if (string.IsNullOrWhiteSpace(context))
        {
            _logger.LogWarning("No relevant context found for query in session {SessionId}: {Query}", sessionId, query);
            context = "No relevant context found in the indexed documents.";
        }

        // 7. Generate answer using LLM
        var answer = await _llmService.GenerateAnswerAsync(condensedQuery, context, cancellationToken);
        var responseTimestamp = DateTimeOffset.UtcNow;

        _logger.LogInformation("Successfully generated answer for session {SessionId}", sessionId);

        // 8. Record the query and response in the database
        await _sessionService.RecordQueryAsync(sessionId, query, condensedQuery, answer, events: null, cancellationToken: cancellationToken);

        return new RagQueryResult
        {
            SessionId = sessionId,
            OriginalQuery = query,
            CondensedQuery = condensedQuery,
            Answer = answer,
            IsNewSession = isNewSession,
            SessionTitle = title
        };
    }

    /// <summary>
    /// Gets the query history for a session.
    /// </summary>
    /// <param name="sessionId">The session identifier.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The list of query history items for the session.</returns>
    public Task<IReadOnlyList<QueryHistoryItem>> GetSessionHistoryAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        return _sessionService.GetSessionHistoryAsync(sessionId, cancellationToken);
    }

    /// <summary>
    /// Gets a query session by ID.
    /// </summary>
    /// <param name="sessionId">The session identifier.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The query session, or null if not found.</returns>
    public Task<QuerySession?> GetSessionAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        return _sessionService.GetSessionAsync(sessionId, cancellationToken);
    }

    #region IRagQueryService Explicit Implementation

    async Task<AdapterInterfaces.RagQueryResult> IRagQueryService.QueryAsync(Guid sessionId, string query, int topK, Func<RagQueryEvent, Task>? onEvent, CancellationToken cancellationToken)
    {
        var result = await QueryAsync(sessionId, query, topK, onEvent, cancellationToken);
        return new AdapterInterfaces.RagQueryResult
        {
            SessionId = result.SessionId,
            OriginalQuery = result.OriginalQuery,
            CondensedQuery = result.CondensedQuery,
            Answer = result.Answer,
            IsNewSession = result.IsNewSession,
            SessionTitle = result.SessionTitle
        };
    }

    #endregion
}
