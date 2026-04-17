using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DuplicatiIndexer.AdapterInterfaces;
using DuplicatiIndexer.Data.Entities;

namespace DuplicatiIndexer.Services;


/// <summary>
/// Represents an action the LLM agent can take.
/// </summary>
public enum AgentActionType
{
    /// <summary>
    /// Search the vector database for relevant content.
    /// </summary>
    SearchDatabase,

    /// <summary>
    /// Query file metadata (timestamps, versions, etc.) without content extraction.
    /// </summary>
    QueryFileMetadata,

    /// <summary>
    /// Provide the final answer to the user's question.
    /// </summary>
    ProvideAnswer
}

/// <summary>
/// Represents an action taken by the ReAct agent.
/// </summary>
public class AgentAction
{
    /// <summary>
    /// Gets or sets the type of action.
    /// </summary>
    [JsonPropertyName("action")]
    public string Action { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the raw action input JSON element (can be string or object).
    /// </summary>
    [JsonPropertyName("action_input")]
    public JsonElement ActionInputElement { get; set; }

    /// <summary>
    /// Gets the action input as a string (handles both string and object JSON values).
    /// </summary>
    public string ActionInput => ActionInputElement.ValueKind switch
    {
        JsonValueKind.String => ActionInputElement.GetString() ?? string.Empty,
        JsonValueKind.Object or JsonValueKind.Array => ActionInputElement.ToString(),
        _ => string.Empty
    };

    /// <summary>
    /// Gets or sets the reasoning/thought process behind the action.
    /// </summary>
    [JsonPropertyName("thought")]
    public string Thought { get; set; } = string.Empty;
}

/// <summary>
/// Represents a step in the ReAct agent's thought process.
/// </summary>
public class ReActStep
{
    /// <summary>
    /// Gets or sets the step number.
    /// </summary>
    public int StepNumber { get; set; }

    /// <summary>
    /// Gets or sets the thought/reasoning for this step.
    /// </summary>
    public string Thought { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the action taken.
    /// </summary>
    public string Action { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the action input.
    /// </summary>
    public string ActionInput { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the observation/result of the action.
    /// </summary>
    public string Observation { get; set; } = string.Empty;
}

/// <summary>
/// Service for performing RAG (Retrieval-Augmented Generation) queries with session management.
/// Uses ReAct pattern where the LLM agent can query the RAG database itself.
/// </summary>
public class ReActQueryService : IRagQueryService
{
    private readonly ILLMClient _llmClient;
    private readonly IHybridSearchService _hybridSearchService;
    private readonly IFileMetadataQueryService _fileMetadataQueryService;
    private readonly RagQuerySessionService _sessionService;
    private readonly IGroupPermissionService _groupPermissionService;
    private readonly ILogger<ReActQueryService> _logger;
    private readonly JsonSerializerOptions _jsonOptions;

    private const int MaxIterations = 10;
    private const string SystemPrompt = @"""You are a helpful AI assistant that answers questions about files and documents stored in a backup system. You have access to search and file metadata tools.

DATABASE CONTENTS:
The database contains files from backups with the following information for each file:
- File path and name (e.g., ""documents/reports/annual_report_2024.pdf"")
- File content (extracted text from documents, code, etc.)
- File metadata: size, last modified date, backup version timestamps

SEARCH STRATEGY:
When searching you get results from RAG and full-text matches, follow these guidelines:
1. START BROAD: Begin with broader search terms that capture the general topic. For example, if the user asks about ""Q1 financial reports"", search for ""financial report"" or ""Q1"" rather than specific filenames.
2. USE KEYWORDS: Focus on important keywords from the user's question. Avoid overly long or specific phrases that might not match exactly.
3. TRY VARIATIONS: If a search returns no results, try different keywords or broader terms. For example, if ""quarterly earnings"" returns nothing, try ""earnings"" or ""financial"".

FILE METADATA QUERIES:
Use query_file_metadata when the user asks about:
- When a file was last modified
- File version history across backups
- File size information
- Files existing at specific backup times
- Questions like ""when was file xxx last modified?"" or ""show me the version history of file yyy""

You must respond in the following JSON format:
{
    ""thought"": ""Your reasoning about what to do next"",
    ""action"": ""search_database"" or ""query_file_metadata"" or ""provide_answer"",
    ""action_input"": ""your search query, file path, or final answer""
}

Available actions:
1. ""search_database"" - Use this to find files and content in the backup database. Provide search keywords as action_input (keep it concise, 1-5 keywords is usually best).
2. ""query_file_metadata"" - Use this to query file metadata like modification times and version history. Provide the exact file path as action_input.
3. ""provide_answer"" - Use this when you have enough information to answer the user's question. Provide the complete answer as action_input.

Guidelines:
- ALWAYS start by searching the database unless the question is purely conversational
- For questions about ""when was file xxx last modified?"", use query_file_metadata with the file path
- You can search multiple times with different queries to gather more information
- Start with broader searches, then narrow down if needed
- Always provide a complete, helpful answer when using provide_answer
- If you cannot find relevant information after several searches, say so clearly and suggest what files might exist based on the search results
""";

    /// <summary>
    /// Initializes a new instance of the <see cref="ReActQueryService"/> class.
    /// </summary>
    /// <param name="llmClient">The LLM client for agent reasoning.</param>
    /// <param name="hybridSearchService">The hybrid search service for searching content.</param>
    /// <param name="fileMetadataQueryService">The file metadata query service for file information.</param>
    /// <param name="sessionService">The session service for managing query sessions.</param>
    /// <param name="groupPermissionService">The group permission service for ACL.</param>
    /// <param name="logger">The logger.</param>
    public ReActQueryService(
        ILLMClient llmClient,
        IHybridSearchService hybridSearchService,
        IFileMetadataQueryService fileMetadataQueryService,
        RagQuerySessionService sessionService,
        IGroupPermissionService groupPermissionService,
        ILogger<ReActQueryService> logger)
    {
        _llmClient = llmClient;
        _hybridSearchService = hybridSearchService;
        _fileMetadataQueryService = fileMetadataQueryService;
        _sessionService = sessionService;
        _groupPermissionService = groupPermissionService;
        _logger = logger;
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            AllowTrailingCommas = true
        };
    }

    /// <summary>
    /// Executes a RAG query using the ReAct pattern where the LLM agent queries the database itself.
    /// </summary>
    /// <param name="sessionId">The session identifier.</param>
    /// <param name="query">The user's question.</param>
    /// <param name="topK">The number of similar documents to retrieve per search.</param>
    /// <param name="onEvent">Optional callback for stream progress events.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The query result containing the answer and session information.</returns>
    public async Task<RagQueryResult> QueryAsync(Guid sessionId, string query, int topK = 5, Func<RagQueryEvent, Task>? onEvent = null, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Processing ReAct RAG query for session {SessionId}: {Query}", sessionId, query);

        // Get the current user's allowed group IDs for ACL filtering
        var allowedGroupIds = _groupPermissionService.GetCurrentUserGroupIds();
        _logger.LogInformation("Querying with allowed groups: {Groups}", string.Join(", ", allowedGroupIds));

        var queryTimestamp = DateTimeOffset.UtcNow;
        var steps = new List<ReActStep>();
        var queryEvents = new List<QueryHistoryEvent>();

        Func<string, string, Task> emitEvent = async (string type, string content) =>
        {
            queryEvents.Add(new QueryHistoryEvent { EventType = type, Content = content });
            if (onEvent != null)
            {
                await onEvent(new RagQueryEvent { EventType = type, Content = content });
            }
        };

        // 1. Get or create session
        var title = await GenerateSessionTitleAsync(query, cancellationToken);
        var (session, isNewSession) = await _sessionService.GetOrCreateSessionAsync(sessionId, title, cancellationToken);

        // 2. Get previous query history for this session
        var history = await _sessionService.GetHistoryForSessionAsync(sessionId, cancellationToken);

        // 3. Run the ReAct agent loop
        var conversationContext = BuildConversationContext(query, history);
        _logger.LogInformation("Context: {Context}", conversationContext);

        string finalAnswer;
        for (int iteration = 0; iteration < MaxIterations; iteration++)
        {
            await emitEvent("info", $"Evaluating chunk contexts and consulting AI (Iteration {iteration + 1})... this usually takes 15-30 seconds depending on context size.");

            var step = await ExecuteReActStepAsync(iteration + 1, conversationContext, steps, cancellationToken);
            steps.Add(step);

            if (!string.IsNullOrWhiteSpace(step.Thought))
            {
                await emitEvent("thought", step.Thought);
            }

            _logger.LogInformation("ReAct Step {Step}: Action={Action}, Input={Input}",
                step.StepNumber, step.Action, step.ActionInput);

            if (step.Action.Equals("provide_answer", StringComparison.OrdinalIgnoreCase))
            {
                finalAnswer = step.ActionInput;
                _logger.LogInformation("ReAct agent provided final answer after {Steps} steps", steps.Count);
                break;
            }

            if (step.Action.Equals("search_database", StringComparison.OrdinalIgnoreCase))
            {
                await emitEvent("action", $"Searching database for: {step.ActionInput}");
                var searchResults = await ExecuteDatabaseSearchAsync(step.ActionInput, allowedGroupIds, topK, cancellationToken);
                step.Observation = searchResults;
                conversationContext += $"\n\nObservation from search: {searchResults}";
            }
            else if (step.Action.Equals("query_file_metadata", StringComparison.OrdinalIgnoreCase))
            {
                var metadataResults = await ExecuteFileMetadataQueryAsync(step.ActionInput, cancellationToken);
                step.Observation = metadataResults;
                conversationContext += $"\n\nObservation from file metadata query: {metadataResults}";
            }
            else
            {
                step.Observation = "Unknown action. Please use 'search_database', 'query_file_metadata', or 'provide_answer'.";
            }

            if (iteration == MaxIterations - 1)
            {
                finalAnswer = "I wasn't able to find a complete answer within the allowed number of search attempts. " +
                    "Based on my searches: " + string.Join(" ", steps.Where(s => !string.IsNullOrEmpty(s.Observation))
                        .Select(s => s.Observation.Substring(0, Math.Min(100, s.Observation.Length))));
            }
        }

        finalAnswer = steps.LastOrDefault(s => s.Action.Equals("provide_answer", StringComparison.OrdinalIgnoreCase))?.ActionInput
            ?? "I apologize, but I wasn't able to formulate a complete answer based on the available information.";

        var result = new RagQueryResult
        {
            SessionId = session.Id,
            IsNewSession = isNewSession,
            Answer = finalAnswer,
            SessionTitle = session.Title
        };

        // Record the query in history
        var condensedQuery = steps.FirstOrDefault()?.Thought ?? query;
        await _sessionService.RecordQueryAsync(session.Id, query, condensedQuery, finalAnswer, queryEvents, cancellationToken);

        return result;
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
            IsNewSession = result.IsNewSession
        };
    }

    #endregion

    private string BuildConversationContext(string query, IReadOnlyList<QueryHistoryItem> history)
    {
        var context = new System.IO.StringWriter();

        if (history.Any())
        {
            context.WriteLine("Previous conversation history:");
            foreach (var item in history.TakeLast(5))
            {
                context.WriteLine($"User: {item.OriginalQuery}");
                context.WriteLine($"Assistant: {item.Response}");
            }
            context.WriteLine();
        }

        context.WriteLine($"Current question: {query}");
        context.WriteLine();
        context.WriteLine("Remember: Start with BROAD search terms (1-3 keywords), then narrow down. Avoid overly specific phrases that might not match the indexed content.");
        context.WriteLine("You can search the database multiple times to gather information before providing your final answer.");

        return context.ToString();
    }

    private async Task<ReActStep> ExecuteReActStepAsync(
        int stepNumber,
        string conversationContext,
        List<ReActStep> previousSteps,
        CancellationToken cancellationToken)
    {
        var promptBuilder = new System.Text.StringBuilder();
        promptBuilder.AppendLine(conversationContext);

        if (previousSteps.Any())
        {
            promptBuilder.AppendLine("\nPrevious actions and observations:");
            foreach (var step in previousSteps)
            {
                promptBuilder.AppendLine($"Step {step.StepNumber}:");
                promptBuilder.AppendLine($"  Thought: {step.Thought}");
                promptBuilder.AppendLine($"  Action: {step.Action}");
                promptBuilder.AppendLine($"  Input: {step.ActionInput}");
                if (!string.IsNullOrEmpty(step.Observation))
                {
                    promptBuilder.AppendLine($"  Observation: {step.Observation}");
                }
            }
        }

        promptBuilder.AppendLine($"\nYou have at most {MaxIterations} steps to complete your task. What is your next action? Respond in the required JSON format.");

        var messages = new[]
        {
            new ChatMessage { Role = ChatRole.System, Content = SystemPrompt },
            new ChatMessage { Role = ChatRole.User, Content = promptBuilder.ToString() }
        };

        var response = await _llmClient.CompleteAsync(messages, cancellationToken);
        var action = ParseAgentAction(response);

        return new ReActStep
        {
            StepNumber = stepNumber,
            Thought = action.Thought,
            Action = action.Action,
            ActionInput = action.ActionInput
        };
    }

    private AgentAction ParseAgentAction(string response)
    {
        try
        {
            // Try to extract JSON from the response (in case there's extra text)
            var jsonStart = response.IndexOf('{');
            var jsonEnd = response.LastIndexOf('}');

            if (jsonStart >= 0 && jsonEnd > jsonStart)
            {
                var json = response.Substring(jsonStart, jsonEnd - jsonStart + 1);
                var action = JsonSerializer.Deserialize<AgentAction>(json, _jsonOptions);
                if (action != null)
                {
                    return action;
                }
            }

            // Fallback: treat the whole response as the answer
            return new AgentAction
            {
                Thought = "Parsing the response directly",
                Action = "provide_answer",
                ActionInputElement = JsonDocument.Parse($"\"{JsonEncodedText.Encode(response.Trim())}\"").RootElement
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse agent action, treating as direct answer");
            return new AgentAction
            {
                Thought = "Error parsing response",
                Action = "provide_answer",
                ActionInputElement = JsonDocument.Parse($"\"{JsonEncodedText.Encode(response.Trim())}\"").RootElement
            };
        }
    }

    private async Task<string> ExecuteDatabaseSearchAsync(string searchQuery, IReadOnlyList<string> allowedGroupIds, int topK, CancellationToken cancellationToken)
    {
        try
        {
            var searchOptions = new HybridSearchOptions
            {
                TopKPerMethod = topK * 2,
                FinalTopK = topK,
                RrfK = 60
            };

            var searchResults = await _hybridSearchService.SearchAsync(searchQuery, allowedGroupIds, searchOptions, cancellationToken);
            var results = searchResults.ToList();

            if (!results.Any())
            {
                return "No relevant documents found for this query.";
            }

            return string.Join("\n\n", results.Select((r, i) =>
                $"[Result {i + 1}] (Score: {r.Score:F4}, Source: {r.Source}) {r.Content}"));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error executing database search for query: {Query}", searchQuery);
            return $"Error searching database: {ex.Message}";
        }
    }

    private async Task<string> ExecuteFileMetadataQueryAsync(string filePath, CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogInformation("Executing file metadata query for path: {FilePath}", filePath);

            // Try to get the last modified info first
            var lastModified = await _fileMetadataQueryService.GetLastModifiedAsync(filePath, cancellationToken: cancellationToken);

            if (lastModified == null)
            {
                // Try searching for files matching the pattern
                var searchResults = await _fileMetadataQueryService.SearchFilesAsync(filePath, limit: 10, cancellationToken: cancellationToken);
                if (searchResults.Any())
                {
                    return $"File not found at exact path '{filePath}'. Found similar files:\n" +
                           string.Join("\n", searchResults.Select(r => $"- {r.Path} (last modified: {r.LastModified:yyyy-MM-dd HH:mm:ss})"));
                }
                return $"No file found matching '{filePath}' in the backup database.";
            }

            // Get version history to show all backup versions
            var versionHistory = await _fileMetadataQueryService.GetCompleteVersionHistoryAsync(filePath, limit: 10, cancellationToken: cancellationToken);

            var result = new StringBuilder();
            result.AppendLine($"File: {lastModified.Path}");
            result.AppendLine($"Last Modified: {lastModified.LastModified:yyyy-MM-dd HH:mm:ss UTC}");
            result.AppendLine($"Size: {FormatBytes(lastModified.Size)}");
            result.AppendLine($"Hash: {lastModified.Hash}");
            result.AppendLine($"Latest Backup Version: {lastModified.Version:yyyy-MM-dd HH:mm:ss}");

            if (versionHistory.Count > 1)
            {
                result.AppendLine($"\nVersion History (showing {versionHistory.Count} versions):");
                foreach (var version in versionHistory.Take(5))
                {
                    result.AppendLine($"  - Backup: {version.Version:yyyy-MM-dd HH:mm:ss}, Modified: {version.LastModified:yyyy-MM-dd HH:mm:ss}, Size: {FormatBytes(version.Size)}");
                }
                if (versionHistory.Count > 5)
                {
                    result.AppendLine($"  ... and {versionHistory.Count - 5} more versions");
                }
            }

            return result.ToString();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error executing file metadata query for path: {FilePath}", filePath);
            return $"Error querying file metadata: {ex.Message}";
        }
    }

    private static string FormatBytes(long bytes)
    {
        string[] sizes = { "B", "KB", "MB", "GB", "TB" };
        if (bytes == 0) return "0 B";
        int i = (int)Math.Floor(Math.Log(bytes) / Math.Log(1024));
        return $"{bytes / Math.Pow(1024, i):F2} {sizes[i]}";
    }

    private async Task<string> GenerateSessionTitleAsync(string query, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query)) return "New Session";

        var trimmed = query.Trim();
        var title = trimmed.Length <= 30 ? trimmed : trimmed.Substring(0, 30) + "...";

        return title;
    }
}
