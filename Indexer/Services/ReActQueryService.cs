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
    /// Gets or sets an optional list of relevant file paths discovered during the step.
    /// </summary>
    [JsonPropertyName("relevant_files")]
    public List<string>? RelevantFiles { get; set; }

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
    /// Gets or sets the relevant files found in this step.
    /// </summary>
    public List<string>? RelevantFiles { get; set; }

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
    private readonly Data.ISurrealRepository _repository;
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

You have two ways to respond:

1. TOOL USE (JSON FORMAT):
If you need to search or use a tool, you MUST respond in the strict JSON format below:
{
    ""thought"": ""Your reasoning about what to do next"",
    ""action"": ""search_database"" or ""query_file_metadata"",
    ""action_input"": ""your search query or file path""
}

Available actions:
- ""search_database"" - Use this to find files and content in the backup database. Provide search keywords as action_input (keep it concise, 1-5 keywords is usually best).
- ""query_file_metadata"" - Use this to query file metadata like modification times and version history. Provide the exact file path as action_input.

2. FINAL ANSWER (RAW TEXT):
If you have enough information to answer the user's question, or if you have exhausted all search options, DO NOT USE JSON. Instead, simply output your final answer as raw markdown text. Do not wrap it in a JSON object. Just provide the answer directly.

Guidelines:
- ALWAYS start by searching the database unless the question is purely conversational
- For questions about ""when was file xxx last modified?"", use query_file_metadata with the file path
- You can search multiple times with different queries to gather more information
- CRITICAL: Never combine JSON and raw text in the same response. Choose one format or the other based on whether you are taking an action or answering the user.
""";

    /// <summary>
    /// Initializes a new instance of the <see cref="ReActQueryService"/> class.
    /// </summary>
    /// <param name="llmClient">The LLM client for agent reasoning.</param>
    /// <param name="hybridSearchService">The hybrid search service for searching content.</param>
    /// <param name="fileMetadataQueryService">The file metadata query service for file information.</param>
    /// <param name="sessionService">The session service for managing query sessions.</param>
    /// <param name="repository">The database repository for metadata resolving.</param>
    /// <param name="logger">The logger.</param>
    public ReActQueryService(
        ILLMClient llmClient,
        IHybridSearchService hybridSearchService,
        IFileMetadataQueryService fileMetadataQueryService,
        RagQuerySessionService sessionService,
        Data.ISurrealRepository repository,
        ILogger<ReActQueryService> logger)
    {
        _llmClient = llmClient;
        _hybridSearchService = hybridSearchService;
        _fileMetadataQueryService = fileMetadataQueryService;
        _sessionService = sessionService;
        _repository = repository;
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

        var queryTimestamp = DateTime.UtcNow;
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
        int consecutiveEmptyPayloads = 0;

        for (int iteration = 0; iteration < MaxIterations; iteration++)
        {
            await emitEvent("info", $"Evaluating chunk contexts and consulting AI (Iteration {iteration + 1})... this usually takes 15-30 seconds depending on context size.");

            ReActStep step;
            try
            {
                step = await ExecuteReActStepAsync(iteration + 1, conversationContext, steps, emitEvent, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error calling LLM during ReAct step {Step}", iteration + 1);
                await emitEvent("info", $"AI model connection error: {ex.Message}");
                
                step = new ReActStep
                {
                    StepNumber = iteration + 1,
                    Action = "provide_answer",
                    ActionInput = $"I apologize, but the AI model encountered a critical error and aborted generation: {ex.Message}. Please try adjusting your context limits or try again later.",
                    Thought = "Generation aborted due to LLM exception."
                };
                steps.Add(step);
                finalAnswer = step.ActionInput;
                break;
            }
            
            if (step.Action.Equals("error_retry", StringComparison.OrdinalIgnoreCase))
            {
                consecutiveEmptyPayloads++;
                if (consecutiveEmptyPayloads >= 2)
                {
                    _logger.LogWarning("Aborting ReAct loop after {Count} consecutive empty payloads", consecutiveEmptyPayloads);
                    await emitEvent("info", "Aborting generation after multiple empty payloads from the model.");
                    step.Action = "provide_answer";
                    step.ActionInput = "I apologize, but the connection to the AI model was interrupted and it repeatedly failed to return a response. Please try your query again or use a different model.";
                    steps.Add(step);
                    finalAnswer = step.ActionInput;
                    break;
                }
            }
            else
            {
                consecutiveEmptyPayloads = 0;
            }

            steps.Add(step);

            if (!string.IsNullOrWhiteSpace(step.Thought))
            {
                await emitEvent("thought", step.Thought);
            }

            // We removed the LLM relevant_files parsing here since it's now handled natively by the database!

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
                var searchResults = await ExecuteDatabaseSearchAsync(step.ActionInput, topK, emitEvent, cancellationToken);
                step.Observation = searchResults;
            }
            else if (step.Action.Equals("query_file_metadata", StringComparison.OrdinalIgnoreCase))
            {
                var metadataResults = await ExecuteFileMetadataQueryAsync(step.ActionInput, cancellationToken);
                step.Observation = metadataResults;
            }
            else if (step.Action.Equals("error_retry", StringComparison.OrdinalIgnoreCase))
            {
                step.Observation = "System: Your last response was completely empty or critically malformed. You MUST generate a valid JSON object matching the required Agent Action schema.";
                await emitEvent("info", "LLM returned empty payload. Attempting recovery format retry...");
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

        finalAnswer = steps.LastOrDefault(s => s.Action.Equals("provide_answer", StringComparison.OrdinalIgnoreCase))?.ActionInput;
        
        if (string.IsNullOrWhiteSpace(finalAnswer))
        {
            finalAnswer = "I apologize, but I wasn't able to formulate a complete answer based on the available information. (Generation may have been dropped or aborted early by your local LLM).";
        }

        var result = new RagQueryResult
        {
            SessionId = session.Id,
            IsNewSession = isNewSession,
            Answer = finalAnswer,
            SessionTitle = session.Title
        };

        // Record the query in history
        var condensedQuery = steps.FirstOrDefault()?.Thought ?? query;
        await _sessionService.RecordQueryAsync(session.Id, query, condensedQuery, finalAnswer, queryEvents, default);

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
        Func<string, string, Task>? emitEvent,
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
                    if (step == previousSteps.Last())
                    {
                        promptBuilder.AppendLine($"  Observation: {step.Observation}");
                    }
                    else
                    {
                        var truncatedObs = step.Observation.Length > 200 
                            ? step.Observation.Substring(0, 200) + "... [truncated historical observation]" 
                            : step.Observation;
                        promptBuilder.AppendLine($"  Observation: {truncatedObs}");
                    }
                }
            }
        }

        promptBuilder.AppendLine($"\nYou have at most {MaxIterations} steps to complete your task. What is your next action? (Respond with JSON if using a tool, or raw text if providing the final answer).");

        var messages = new[]
        {
            new ChatMessage { Role = ChatRole.System, Content = SystemPrompt },
            new ChatMessage { Role = ChatRole.User, Content = promptBuilder.ToString() }
        };

        var stream = _llmClient.StreamCompleteAsync(messages, cancellationToken);
        var isFirstToken = true;
        var isJsonMode = false;
        var fullResponseBuilder = new System.Text.StringBuilder();

        await foreach (var chunk in stream.WithCancellation(cancellationToken))
        {
            if (string.IsNullOrEmpty(chunk)) continue;

            if (isFirstToken)
            {
                var trimmed = chunk.TrimStart();
                if (string.IsNullOrEmpty(trimmed)) continue;

                isFirstToken = false;
                if (trimmed.StartsWith("{") || trimmed.StartsWith("```json") || trimmed.StartsWith("<|tool_call>"))
                {
                    isJsonMode = true;
                }
            }

            fullResponseBuilder.Append(chunk);

            if (!isJsonMode && emitEvent != null)
            {
                // Stream plain text final answer chunks directly to the UI
                await emitEvent("answer_chunk", chunk);
            }
        }

        var response = fullResponseBuilder.ToString();

        if (!isJsonMode)
        {
            // Synthesize the final answer action
            return new ReActStep
            {
                StepNumber = stepNumber,
                Thought = "Provided raw text final answer directly to user.",
                Action = "provide_answer",
                ActionInput = response.Trim()
            };
        }

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
            if (response.Contains("<|tool_call>call:"))
            {
                var actionStartIndex = response.IndexOf("call:") + 5;
                var argsStartIndex = response.IndexOf('{', actionStartIndex);
                var endTokenIndex = response.IndexOf("<tool_call|>", argsStartIndex);
                if (endTokenIndex == -1) endTokenIndex = response.LastIndexOf('}');
                
                if (argsStartIndex > 0 && endTokenIndex > argsStartIndex)
                {
                    var actionName = response.Substring(actionStartIndex, argsStartIndex - actionStartIndex).Trim();
                    var argsRaw = response.Substring(argsStartIndex + 1, endTokenIndex - argsStartIndex - 1);
                    
                    var inputMatch = System.Text.RegularExpressions.Regex.Match(argsRaw, @"""([^""]+)""");
                    var actionInput = inputMatch.Success ? inputMatch.Groups[1].Value : argsRaw.Trim();
                    
                    return new AgentAction
                    {
                        Thought = "Parsed native tool_call token",
                        Action = actionName,
                        ActionInputElement = JsonDocument.Parse(JsonSerializer.Serialize(actionInput)).RootElement
                    };
                }
            }

            // Try to extract JSON from the response (in case there's extra text)
            var jsonStart = response.IndexOf('{');
            var jsonEnd = response.LastIndexOf('}');

            if (jsonStart >= 0 && jsonEnd > jsonStart)
            {
                var json = response.Substring(jsonStart, jsonEnd - jsonStart + 1);
                var action = JsonSerializer.Deserialize<AgentAction>(json, _jsonOptions);
                if (action != null && !string.IsNullOrEmpty(action.Action))
                {
                    return action;
                }
            }

            // Fallback: treat the whole response as the answer or extract partial fields
            var cleanedResponse = response.Trim();
            
            if (cleanedResponse.Contains("\"thought\"") || cleanedResponse.StartsWith("{") || cleanedResponse.StartsWith("```json"))
            {
                string textContent = cleanedResponse;
                bool wasInterrupted = !cleanedResponse.EndsWith("}");
                
                // Try extracting action_input first, as that's the ultimate answer
                var inputMatch = System.Text.RegularExpressions.Regex.Match(cleanedResponse, @"""action_input""\s*:\s*""([\s\S]*?)(?:""\s*,|""\s*}|$)");
                if (inputMatch.Success && !string.IsNullOrWhiteSpace(inputMatch.Groups[1].Value))
                {
                    textContent = inputMatch.Groups[1].Value;
                }
                else
                {
                    // Fallback to extracting the thought
                    var thoughtMatch = System.Text.RegularExpressions.Regex.Match(cleanedResponse, @"""thought""\s*:\s*""([\s\S]*?)(?:""\s*,|""\s*}|$)");
                    if (thoughtMatch.Success)
                    {
                        textContent = thoughtMatch.Groups[1].Value;
                    }
                }
                
                // Unescape typical json newlines and quotes loosely
                textContent = textContent.Replace("\\n", "\n").Replace("\\\"", "\"").Replace("\\\\", "\\").Trim();

                if (wasInterrupted)
                {
                    textContent += "... [Generation interrupted due to context limits]";
                }

                return new AgentAction
                {
                    Thought = "Parsed incomplete JSON response",
                    Action = "provide_answer",
                    ActionInputElement = JsonDocument.Parse(JsonSerializer.Serialize(textContent)).RootElement
                };
            }

            if (string.IsNullOrWhiteSpace(cleanedResponse))
            {
                return new AgentAction
                {
                    Thought = "The LLM dropped the generation or returned an empty payload.",
                    Action = "error_retry",
                    ActionInputElement = JsonDocument.Parse("\"\"").RootElement
                };
            }

            return new AgentAction
            {
                Thought = $"Parsing the response directly [Debug: startsWithMark={cleanedResponse.StartsWith("```json")}, containsThought={cleanedResponse.Contains("\"thought\"")}, startChars='{(cleanedResponse.Length > 0 ? (int)cleanedResponse[0] : 0)}']",
                Action = "error_retry",
                ActionInputElement = JsonDocument.Parse(JsonSerializer.Serialize(cleanedResponse)).RootElement
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse agent action, treating as direct answer");
            return new AgentAction
            {
                Thought = "Error parsing response",
                Action = "provide_answer",
                ActionInputElement = JsonDocument.Parse(JsonSerializer.Serialize(response.Trim())).RootElement
            };
        }
    }

    private async Task<string> ExecuteDatabaseSearchAsync(string searchQuery, int topK, Func<string, string, Task> emitEvent, CancellationToken cancellationToken)
    {
        try
        {
            var searchOptions = new HybridSearchOptions
            {
                TopKPerMethod = topK * 2,
                FinalTopK = topK,
                RrfK = 60
            };

            var searchResults = await _hybridSearchService.SearchAsync(searchQuery, searchOptions, cancellationToken);
            var results = searchResults.ToList();

            if (results.Any())
            {
                // Push the explicit vector mathematical scores down to the frontend!
                double maxScore = results.Max(r => r.Score);
                var relevantPayload = new List<object>();

                foreach (var r in results)
                {
                    if (r.Metadata != null && r.Metadata.TryGetValue("FileId", out var fileIdObj))
                    {
                        var fileIdStr = fileIdObj?.ToString();
                        if (!string.IsNullOrEmpty(fileIdStr))
                        {
                            // Map the raw UUID string directly to the SurrealDB Record ID to bypass .NET Guid endianness bugs
                            // This ensures O(1) primary key resolution.
                            var recordId = fileIdStr.Replace("-", "");
                            var entries = await _repository.QueryAsync<Data.Entities.BackupFileEntry>($"SELECT * FROM type::thing('backupfileentry', '{recordId}')", null, cancellationToken);
                            var entry = entries.FirstOrDefault();
                            if (entry != null && !string.IsNullOrEmpty(entry.Path))
                            {
                                relevantPayload.Add(new { 
                                    path = entry.Path, 
                                    score = maxScore > 0 ? (double)(r.Score / maxScore) : 0 
                                });
                            }
                        }
                    }
                }
                    
                if (relevantPayload.Any())
                {
                    await emitEvent("relevant_files", JsonSerializer.Serialize(relevantPayload));
                }
            }

            if (!results.Any())
            {
                return "No relevant documents found for this query.";
            }

            int maxCharsPerResult = 4000 / Math.Max(1, results.Count);
            return string.Join("\n\n", results.Select((r, i) =>
            {
                string content = r.Content ?? string.Empty;
                if (content.Length > maxCharsPerResult)
                {
                    content = content.Substring(0, maxCharsPerResult) + "... [truncated]";
                }
                return $"[Result {i + 1}] (Score: {r.Score:F4}, Source: {r.Source}) {content}";
            }));
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
