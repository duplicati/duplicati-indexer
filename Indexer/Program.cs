using JasperFx;
using DuplicatiIndexer.Data.Entities;
using DuplicatiIndexer.AdapterInterfaces;
using DuplicatiIndexer.ChatGPTAdapter;
using DuplicatiIndexer.ClaudeAdapter;
using DuplicatiIndexer.Configuration;
using DuplicatiIndexer.Data;
using DuplicatiIndexer.GeminiAdapter;
using DuplicatiIndexer.HealthChecks;
using DuplicatiIndexer.Messages;
using DuplicatiIndexer.OllamaAdapter;
using DuplicatiIndexer.Services;
using DuplicatiIndexer.Services.Security;
using DuplicatiIndexer.UnstructuredAdapter;
using DuplicatiIndexer.MarkItDownAdapter;
using SurrealDb.Net;
using Wolverine;
using System.Text.Json;
using Serilog;
using Polly;
using Polly.Extensions.Http;

var builder = WebApplication.CreateBuilder(args);

// Bind environment configuration (supports both appsettings.json and environment variables with INDEXER__ prefix)
var environmentConfig = new EnvironmentConfig();
builder.Configuration.Bind(environmentConfig);
builder.Services.AddSingleton(environmentConfig);

builder.Services.AddSingleton(environmentConfig.OllamaEmbed);

// Configure Serilog
var logLevel = environmentConfig.Logging.LogLevel.Default;
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Is(Enum.Parse<Serilog.Events.LogEventLevel>(logLevel, true))
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .CreateLogger();

builder.Logging.ClearProviders();
builder.Logging.AddSerilog();

// Configure SurrealDB
var surrealConnectionString = environmentConfig.ConnectionStrings.DocumentStore ?? "ws://localhost:8000/rpc";

builder.Services.AddSurreal(surrealConnectionString);
builder.Services.AddScoped<ISurrealRepository, SurrealRepository>();
builder.Services.AddSingleton<DbStatsLiveMonitor>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<DbStatsLiveMonitor>());

// Register Services
builder.Services.AddScoped<DlistProcessor>();
builder.Services.AddScoped<DiffCalculator>();
builder.Services.AddScoped<FileRestorer>();
builder.Services.AddScoped<FilenameFilterService>();
builder.Services.AddSingleton<BackendToolService>();
builder.Services.AddScoped<IFileMetadataQueryService, FileMetadataQueryService>();

// Register Threat State Monitor based on configuration
var threatMonitorProvider = environmentConfig.Security.ThreatMonitor.ToLowerInvariant();
if (threatMonitorProvider == "threatstatemonitor")
{
    builder.Services.AddSingleton<IThreatStateMonitor, ThreatStateMonitor>();
    Log.Information("Using ThreatStateMonitor (enabled threat monitoring)");
}
else
{
    // Default: no-op implementation
    builder.Services.AddSingleton<IThreatStateMonitor, NoOpThreatStateMonitor>();
    Log.Information("Using NoOpThreatStateMonitor (threat monitoring disabled)");
}

// Configure retry policy for HTTP clients
var retryPolicy = HttpPolicyExtensions
    .HandleTransientHttpError()
    .WaitAndRetryAsync(3, retryAttempt =>
        TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)));

// Register LLM Client based on provider configuration
var llmProvider = environmentConfig.Llm.Provider.ToLowerInvariant();

if (llmProvider == "chatgpt" || llmProvider == "openai")
{
    builder.Services.AddSingleton(environmentConfig.ChatGPT);
    builder.Services.AddHttpClient<ILLMClient, ChatGPTService>(client => client.Timeout = TimeSpan.FromMinutes(10))
        .AddPolicyHandler(retryPolicy);
}
else if (llmProvider == "lmstudio")
{
    var lmStudioConfig = environmentConfig.LMStudio;
    var chatGptConfig = new ChatGPTConfig
    {
        BaseUrl = lmStudioConfig.BaseUrl,
        ApiKey = "lm-studio", // Dummy key
        Model = lmStudioConfig.Model
    };

    builder.Services.AddSingleton(chatGptConfig);
    builder.Services.AddHttpClient<ILLMClient, ChatGPTService>(client => client.Timeout = TimeSpan.FromMinutes(10))
        .AddPolicyHandler(retryPolicy);
    Log.Information("Using LMStudio LLM provider with model: {Model} at {BaseUrl}", chatGptConfig.Model, chatGptConfig.BaseUrl);
}
else if (llmProvider == "claude")
{
    builder.Services.AddSingleton(environmentConfig.Claude);
    builder.Services.AddHttpClient<ILLMClient, ClaudeService>(client => client.Timeout = TimeSpan.FromMinutes(10))
        .AddPolicyHandler(retryPolicy);
}
else if (llmProvider.StartsWith("local:") || llmProvider == "ollama")
{
    // Ollama-based local LLM provider
    // Format: local:<model> (e.g., local:qwen3, local:llama3.2, local:gemma3, local:phi4-mini)
    var ollamaConfig = environmentConfig.OllamaLLM;

    // Extract model name from "local:<model>" format, or use configured model for "ollama"
    if (llmProvider.StartsWith("local:"))
    {
        ollamaConfig.Model = llmProvider.Substring(6); // Remove "local:" prefix
    }

    builder.Services.AddSingleton(ollamaConfig);
    builder.Services.AddHttpClient<ILLMClient, OllamaLLMService>(client => client.Timeout = TimeSpan.FromMinutes(10))
        .AddPolicyHandler(retryPolicy);
    Log.Information("Using Ollama LLM provider with model: {Model} at {BaseUrl}", ollamaConfig.Model, ollamaConfig.BaseUrl);
}
else
{
    builder.Services.AddSingleton(environmentConfig.Gemini);
    builder.Services.AddHttpClient<ILLMClient, GeminiService>(client => client.Timeout = TimeSpan.FromMinutes(10))
        .AddPolicyHandler(retryPolicy);
}

// Register LLMQueryService as the ILLMQueryService implementation
builder.Services.AddScoped<ILLMQueryService, LLMQueryService>();

// Register RAG Query Session Service
builder.Services.AddScoped<RagQuerySessionService>();

// Register RAG Query Services based on configuration
// V1: Traditional RAG with automatic embedding and search
// V2: ReAct pattern where LLM controls the search process
var ragQueryVersion = environmentConfig.RagQuery.Version?.ToLowerInvariant() ?? "v1";

if (ragQueryVersion == "v2")
{
    builder.Services.AddScoped<IRagQueryService, ReActQueryService>();
    Log.Information("RAG Query Service V2 (ReAct pattern) registered");
}
else
{
    builder.Services.AddScoped<IRagQueryService, DirectQueryService>();
    Log.Information("RAG Query Service V1 (traditional) registered");
}

// Also register concrete implementations for direct access if needed
builder.Services.AddScoped<DirectQueryService>();
builder.Services.AddScoped<ReActQueryService>();

// Register Embedding Service based on configuration
var embedProvider = environmentConfig.Embed.Provider.ToLowerInvariant();

if (embedProvider == "lmstudio")
{
    var embedConfig = new ChatGPTEmbeddingConfig
    {
        BaseUrl = environmentConfig.LMStudio.BaseUrl,
        ApiKey = "lm-studio", // Dummy key
        EmbedModel = environmentConfig.LMStudio.EmbedModel
    };
    builder.Services.AddSingleton(embedConfig);
    builder.Services.AddHttpClient<IEmbeddingService, ChatGPTEmbeddingService>(client => client.Timeout = TimeSpan.FromMinutes(10))
        .AddPolicyHandler(retryPolicy);
    Log.Information("Using LMStudio Embedding provider with model: {Model} at {BaseUrl}", embedConfig.EmbedModel, embedConfig.BaseUrl);
}
else
{
    // Register Default Embedding Service with Ollama
    builder.Services.AddHttpClient<IEmbeddingService, OllamaEmbeddingService>(client => client.Timeout = TimeSpan.FromMinutes(10))
        .AddPolicyHandler(retryPolicy);
}

// Register HttpClient for Ollama health check
builder.Services.AddHttpClient<OllamaEmbeddingHealthCheck>();


// Register Content Indexer based on provider configuration
var contentIndexerProvider = environmentConfig.Indexing.Provider.ToLowerInvariant();

if (contentIndexerProvider == "markitdown")
{
    builder.Services.AddScoped<IContentIndexer, MarkItDownIndexer>();
    Log.Information("Using MarkItDown content indexer");
}
else
{
    // Default: Unstructured
    builder.Services.AddHttpClient<IContentIndexer, UnstructuredIndexer>(client =>
    {
        client.BaseAddress = new Uri(environmentConfig.Unstructured.BaseUrl);
    })
        .AddPolicyHandler(retryPolicy);
    Log.Information("Using Unstructured content indexer at {BaseUrl}", environmentConfig.Unstructured.BaseUrl);
}

// Register Vector Store
builder.Services.AddScoped<IVectorStore, DuplicatiIndexer.SurrealAdapter.SurrealVectorStore>();
Log.Information("Using SurrealDB for vector store");

// Register Sparse Index (SurrealDB full-text search)
builder.Services.AddScoped<ISparseIndex, DuplicatiIndexer.SurrealAdapter.SurrealSparseIndex>();

// Register Hybrid Search Service (combines vector and sparse search with RRF)
builder.Services.AddScoped<IHybridSearchService, HybridSearchService>();

// Register Tokenizer
builder.Services.AddSingleton<ITokenizer, ApproximationTokenizer>();

// Register Text Chunker
builder.Services.AddSingleton<ITextChunker, SimpleTextChunker>();

builder.Host.UseWolverine(options =>
{
    options.Policies.UseDurableLocalQueues();

    // Ensure Wolverine creates its schema tables (wolverine.incoming_envelopes, etc.) on startup
    options.AutoBuildMessageStorageOnStartup = AutoCreate.CreateOrUpdate;

    // Increase default execution timeout for embedding/massive metadata operations
    options.DefaultExecutionTimeout = TimeSpan.FromHours(6);

    // Disable conventional discovery and explicitly include types
    options.Discovery.DisableConventionalDiscovery();
    options.Discovery.IncludeType<DuplicatiIndexer.Handlers.BackupVersionCreatedHandler>();
    options.Discovery.IncludeType<DuplicatiIndexer.Handlers.DlistProcessingCompletedHandler>();
    options.Discovery.IncludeType<DuplicatiIndexer.Handlers.StartFileRestorationHandler>();
    options.Discovery.IncludeType<DuplicatiIndexer.Handlers.ExtractTextAndIndexHandler>();
    // NATIVELY OVERRIDE PIPELINE SEQUENTIAL PROCESSING ORCHESTRATION TO AGGRESSIVELY SATURATE ALL 4 GPU NODES
    options.LocalQueue("extract-text-and-index").MaximumParallelMessages(32); // Massive parallel streams to constantly flush payloads into the LMStudio round-robin!
    options.BatchMessagesOf<DuplicatiIndexer.Messages.ExtractTextAndIndex>(batching =>
    {
        batching.BatchSize = 25;
        batching.TriggerTime = TimeSpan.FromMilliseconds(200);
        batching.LocalExecutionQueueName = "extract-text-and-index";
    });
    options.PublishMessage<DuplicatiIndexer.Messages.ExtractTextAndIndex>().ToLocalQueue("extract-text-and-index");
    
    // Scale Phase 2 File Restoration Workers horizontally (8 concurrent local extractions)
    options.LocalQueue("start-file-restoration").MaximumParallelMessages(8);
    options.PublishMessage<DuplicatiIndexer.Messages.StartFileRestoration>().ToLocalQueue("start-file-restoration");
}, ExtensionDiscovery.ManualOnly);

// Add Health Checks
var healthChecks = builder.Services.AddHealthChecks()
    .AddCheck<DatabaseHealthCheck>("database")
    .AddCheck<OllamaEmbeddingHealthCheck>("ollama");

// Add content indexer health check based on provider
if (contentIndexerProvider == "markitdown")
{
    healthChecks.AddCheck<MarkItDownHealthCheck>("markitdown");
}
else
{
    // Default: Unstructured
    builder.Services.AddHttpClient<UnstructuredHealthCheck>(client =>
    {
        client.BaseAddress = new Uri(environmentConfig.Unstructured.BaseUrl);
    }).AddPolicyHandler(retryPolicy);
    healthChecks.AddCheck<UnstructuredHealthCheck>("unstructured");
}



var app = builder.Build();

// Surrogate initialization for SurrealDB could go here
    _ = Task.Run(async () =>
    {
        try
        {
            using var scope = app.Services.CreateScope();
            var dbInit = scope.ServiceProvider.GetRequiredService<ISurrealDbClient>();
            // Construct critical indices to collapse the O(N^2) duplication checks into O(1) hash lookups!
            await dbInit.RawQuery(@"
                DEFINE INDEX IF NOT EXISTS idx_backupfile_path ON backupfileentry FIELDS BackupSourceId, Path;
                DEFINE INDEX IF NOT EXISTS idx_backupfile_deleted ON backupfileentry FIELDS VersionDeleted;
                DEFINE INDEX IF NOT EXISTS idx_backupversionfile_path ON backupversionfile FIELDS BackupSourceId, Version, Path;
                DEFINE INDEX IF NOT EXISTS idx_backupfile_filter ON backupfileentry FIELDS BackupSourceId, VersionAdded, IsIndexed, VersionDeleted;
            ");
            Log.Information("SurrealDB O(1) indices successfully seeded.");
        }
        catch (Exception ex)
        {
            Log.Warning("Failed to asynchronously seed indices: {msg}", ex.Message);
        }
    });
Log.Information("Using SurrealDB successfully");

// Map health check endpoint
app.MapHealthChecks("/health");

// TODO: Add authentication and authorization to these endpoints

app.MapGet("/api/stats/stream", async (HttpResponse response, DbStatsLiveMonitor monitor, CancellationToken cancellationToken) =>
{
    response.Headers.Append("Content-Type", "text/event-stream");
    response.Headers.Append("Cache-Control", "no-cache");
    response.Headers.Append("Connection", "keep-alive");

    var channel = System.Threading.Channels.Channel.CreateBounded<bool>(new System.Threading.Channels.BoundedChannelOptions(1) { FullMode = System.Threading.Channels.BoundedChannelFullMode.DropOldest });

    Action trigger = () => channel.Writer.TryWrite(true);
    monitor.OnStatsUpdated += trigger;

    try
    {
        // Emit initial exact count organically
        await SendStatsDataAsync(response, monitor);

        // Await background trigger natively
        await foreach (var _ in channel.Reader.ReadAllAsync(cancellationToken))
        {
            await SendStatsDataAsync(response, monitor);
            // Lock max transmission push to 500ms bounds natively
            await Task.Delay(500, cancellationToken);
        }
    }
    catch (TaskCanceledException) { }
    finally
    {
        monitor.OnStatsUpdated -= trigger;
    }

    static async Task SendStatsDataAsync(HttpResponse r, DbStatsLiveMonitor m)
    {
        var payload = JsonSerializer.Serialize(new { documentCount = m.VectorCount, sparseCount = m.SparseCount, metadataCount = m.MetadataCount, versionFileCount = m.VersionFileCount });
        await r.WriteAsync($"data: {payload}\n\n");
        await r.Body.FlushAsync();
    }
});

app.MapGet("/api/stats", (DbStatsLiveMonitor monitor) => 
    Results.Ok(new { 
        metadataCount = monitor.MetadataCount, 
        vectorCount = monitor.VectorCount, 
        sparseCount = monitor.SparseCount, 
        versionFileCount = monitor.VersionFileCount,
        extractedChunkCount = monitor.ExtractedChunkCount,
        indexedFileCount = monitor.IndexedFileCount
    }));

// API endpoint to inject a BackupVersionCreated message via Wolverine
app.MapPost("/api/messages/backup-version-created", async (BackupVersionCreated message, IMessageBus bus) =>
{
    if (string.IsNullOrWhiteSpace(message.BackupId))
        return Results.BadRequest(new { error = "BackupId is required" });

    if (string.IsNullOrWhiteSpace(message.DlistFilename))
        return Results.BadRequest(new { error = "DlistFilename is required" });

    await bus.PublishAsync(message);

    Log.Information("Published BackupVersionCreated message: BackupId={BackupId}, DlistFilename={DlistFilename}",
        message.BackupId, message.DlistFilename);

    return Results.Accepted(value: new
    {
        status = "accepted",
        backupId = message.BackupId,
        dlistFilename = message.DlistFilename
    });
});

app.MapPost("/api/backup-sources", async (CreateBackupSourceRequest request, ISurrealRepository repository) =>
{
    if (string.IsNullOrWhiteSpace(request.Name))
        return Results.BadRequest(new { error = "Name is required" });

    if (string.IsNullOrWhiteSpace(request.DuplicatiBackupId))
        return Results.BadRequest(new { error = "DuplicatiBackupId is required" });

    var backupSource = new BackupSource
    {
        Id = request.Id ?? Guid.NewGuid(),
        Name = request.Name,
        DuplicatiBackupId = request.DuplicatiBackupId,
        CreatedAt = request.CreatedAt ?? DateTimeOffset.UtcNow,
        LastParsedVersion = request.LastParsedVersion,
        EncryptionPassword = request.EncryptionPassword,
        TargetUrl = request.TargetUrl ?? string.Empty
    };

    await repository.StoreAsync(backupSource, default);

    Log.Information("Created BackupSource: Id={Id}, Name={Name}, DuplicatiBackupId={DuplicatiBackupId}",
        backupSource.Id, backupSource.Name, backupSource.DuplicatiBackupId);

    return Results.Created($"/api/backup-sources/{backupSource.Id}", backupSource);
});

// API endpoint to execute RAG queries (uses configured version: V1 or V2)
app.MapPost("/api/rag/query", async (RagQueryRequest request, IRagQueryService ragQueryService, RagQuerySessionService sessionService, CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(request.Query))
        return Results.BadRequest(new { error = "Query is required" });

    var topK = request.TopK ?? 10;
    if (topK < 1 || topK > 20)
        return Results.BadRequest(new { error = "TopK must be between 1 and 20" });

    Guid sessionId;
    if (request.SessionId.HasValue)
    {
        // Verify that the session exists
        var existingSession = await sessionService.GetSessionAsync(request.SessionId.Value, cancellationToken);
        if (existingSession == null)
            return Results.BadRequest(new { error = "Session not found" });

        sessionId = request.SessionId.Value;
    }
    else
    {
        // Create a new session ID
        sessionId = Guid.NewGuid();
    }

    var result = await ragQueryService.QueryAsync(sessionId, request.Query, topK, null, cancellationToken);

    Log.Information("RAG query processed: SessionId={SessionId}, IsNewSession={IsNewSession}, Query='{Query}', TopK={TopK}",
        result.SessionId, result.IsNewSession, request.Query, topK);

    return Results.Ok(new RagQueryResponse(
        result.SessionId,
        result.Answer,
        result.IsNewSession));
});

// API endpoint to get all sessions
app.MapGet("/api/rag/sessions", async (RagQuerySessionService sessionService, CancellationToken cancellationToken) =>
{
    var sessions = await sessionService.GetAllSessionsAsync(cancellationToken);
    return Results.Ok(sessions.Select(s => new
    {
        s.Id,
        s.Title,
        s.CreatedAt,
        s.LastActivityAt
    }));
});

// API endpoint to get session history
app.MapGet("/api/rag/sessions/{sessionId:guid}", async (Guid sessionId, RagQuerySessionService sessionService, CancellationToken cancellationToken) =>
{
    var session = await sessionService.GetSessionAsync(sessionId, cancellationToken);
    if (session == null)
        return Results.NotFound(new { error = "Session not found" });

    var history = await sessionService.GetSessionHistoryAsync(sessionId, cancellationToken);

    return Results.Ok(new SessionDetailsResponse(
        session.Id,
        session.Title,
        session.CreatedAt,
        session.LastActivityAt,
        history.Select(h => new HistoryItemResponse(
            h.Id,
            h.OriginalQuery,
            h.CondensedQuery,
            h.Response,
            h.QueryTimestamp,
            h.ResponseTimestamp,
            h.Events)).ToList()));
});

// API endpoint to execute RAG queries using ReAct pattern (V2)
app.MapPost("/api/rag/v2/query", async (RagQueryRequest request, ReActQueryService ragQueryServiceV2, RagQuerySessionService sessionService, CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(request.Query))
        return Results.BadRequest(new { error = "Query is required" });

    var topK = request.TopK ?? 10;
    if (topK < 1 || topK > 20)
        return Results.BadRequest(new { error = "TopK must be between 1 and 20" });

    Guid sessionId;
    if (request.SessionId.HasValue)
    {
        // Verify that the session exists
        var existingSession = await sessionService.GetSessionAsync(request.SessionId.Value, cancellationToken);
        if (existingSession == null)
            return Results.BadRequest(new { error = "Session not found" });

        sessionId = request.SessionId.Value;
    }
    else
    {
        // Create a new session ID
        sessionId = Guid.NewGuid();
    }

    var result = await ragQueryServiceV2.QueryAsync(sessionId, request.Query, topK, null, cancellationToken);

    Log.Information("RAG V2 query processed: SessionId={SessionId}, IsNewSession={IsNewSession}, Query='{Query}', TopK={TopK}",
        result.SessionId, result.IsNewSession, request.Query, topK);

    return Results.Ok(new RagQueryResponse(
        result.SessionId,
        result.Answer,
        result.IsNewSession));
});

// API endpoint to execute RAG queries with Server-Sent Events (SSE) streaming progress over POST
app.MapPost("/api/rag/query/stream", async (RagQueryRequest request, IRagQueryService ragQueryService, RagQuerySessionService sessionService, HttpContext context, CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(request.Query))
    {
        context.Response.StatusCode = 400;
        await context.Response.WriteAsync("Query is required");
        return;
    }

    var k = request.TopK ?? 10;
    if (k < 1 || k > 20)
    {
        context.Response.StatusCode = 400;
        await context.Response.WriteAsync("TopK must be between 1 and 20");
        return;
    }

    Guid actualSessionId;
    if (request.SessionId.HasValue)
    {
        var existingSession = await sessionService.GetSessionAsync(request.SessionId.Value, cancellationToken);
        if (existingSession == null)
        {
            context.Response.StatusCode = 400;
            await context.Response.WriteAsync("Session not found");
            return;
        }
        actualSessionId = request.SessionId.Value;
    }
    else
    {
        actualSessionId = Guid.NewGuid();
    }

    context.Response.Headers.Append("Content-Type", "text/event-stream");
    context.Response.Headers.Append("Cache-Control", "no-cache");
    context.Response.Headers.Append("Connection", "keep-alive");
    context.Response.Headers.Append("X-Accel-Buffering", "no");

    // Flush immediately and send an empty SSE comment to force the proxy/client to accept the stream
    await context.Response.Body.FlushAsync();
    await context.Response.WriteAsync(":\n\n");
    await context.Response.Body.FlushAsync();

    var jsonOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    Func<RagQueryEvent, Task> onEvent = async (e) =>
    {
        var json = JsonSerializer.Serialize(e, jsonOptions);
        await context.Response.WriteAsync($"data: {json}\n\n");
        await context.Response.Body.FlushAsync();
    };

    var result = await ragQueryService.QueryAsync(actualSessionId, request.Query, k, onEvent, cancellationToken);

    var finalEvent = new RagQueryEvent
    {
        EventType = "final",
        Content = result.Answer,
        EventContext = result.SessionId.ToString()
    };
    var finalJson = JsonSerializer.Serialize(finalEvent, jsonOptions);
    await context.Response.WriteAsync($"data: {finalJson}\n\n");
    await context.Response.Body.FlushAsync();
});

// API endpoint to get session history for V2 sessions
app.MapGet("/api/rag/v2/sessions/{sessionId:guid}", async (Guid sessionId, RagQuerySessionService sessionService, CancellationToken cancellationToken) =>
{
    var session = await sessionService.GetSessionAsync(sessionId, cancellationToken);
    if (session == null)
        return Results.NotFound(new { error = "Session not found" });

    var history = await sessionService.GetSessionHistoryAsync(sessionId, cancellationToken);

    return Results.Ok(new SessionDetailsResponse(
        session.Id,
        session.Title,
        session.CreatedAt,
        session.LastActivityAt,
        history.Select(h => new HistoryItemResponse(
            h.Id,
            h.OriginalQuery,
            h.CondensedQuery,
            h.Response,
            h.QueryTimestamp,
            h.ResponseTimestamp,
            h.Events)).ToList()));
});

// API endpoints for file metadata queries
// Find a file by path pattern (supports wildcards * and ?)
app.MapGet("/api/files/find", async (string path, Guid? backupSourceId, IFileMetadataQueryService queryService, CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(path))
        return Results.BadRequest(new { error = "Path parameter is required" });

    var result = await queryService.FindFileAsync(path, backupSourceId, cancellationToken);

    if (result == null)
        return Results.NotFound(new { error = $"No file found matching pattern '{path}'" });

    return Results.Ok(result);
});

// Get version history for a specific file
app.MapGet("/api/files/history", async (string path, Guid? backupSourceId, int? limit, IFileMetadataQueryService queryService, CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(path))
        return Results.BadRequest(new { error = "Path parameter is required" });

    var history = await queryService.GetFileVersionHistoryAsync(path, backupSourceId, limit ?? 20, cancellationToken);

    return Results.Ok(new FileHistoryResponse(path, history));
});

// Search files by name pattern
app.MapGet("/api/files/search", async (string pattern, Guid? backupSourceId, int? limit, IFileMetadataQueryService queryService, CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(pattern))
        return Results.BadRequest(new { error = "Pattern parameter is required" });

    var results = await queryService.SearchFilesAsync(pattern, backupSourceId, limit ?? 50, cancellationToken);

    return Results.Ok(new FileSearchResponse(pattern, results.Count, results));
});

// List files in a directory
app.MapGet("/api/files/list", async (string directory, Guid? backupSourceId, bool? recursive, IFileMetadataQueryService queryService, CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(directory))
        return Results.BadRequest(new { error = "Directory parameter is required" });

    var results = await queryService.ListDirectoryAsync(directory, backupSourceId, recursive ?? false, cancellationToken);

    return Results.Ok(new FileListResponse(directory, recursive ?? false, results.Count, results));
});

// Get files modified between dates
app.MapGet("/api/files/modified-between", async (DateTimeOffset start, DateTimeOffset end, Guid? backupSourceId, int? limit, IFileMetadataQueryService queryService, CancellationToken cancellationToken) =>
{
    if (start >= end)
        return Results.BadRequest(new { error = "Start date must be before end date" });

    var results = await queryService.GetFilesModifiedBetweenAsync(start, end, backupSourceId, limit ?? 100, cancellationToken);

    return Results.Ok(new FilesModifiedResponse(start, end, results.Count, results));
});

// API endpoint for RRF hybrid search (OpenClaw integration)
app.MapPost("/api/search/rrf", async (RrfSearchRequest request, IHybridSearchService hybridSearchService, CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(request.Query))
        return Results.BadRequest(new { error = "Query is required" });

    var options = new HybridSearchOptions
    {
        TopKPerMethod = request.TopKPerMethod ?? 10,
        FinalTopK = request.FinalTopK ?? 5,
        RrfK = request.RrfK ?? 60,
        VectorWeight = request.VectorWeight ?? 1.0,
        SparseWeight = request.SparseWeight ?? 1.0,
        UseWeightedFusion = request.UseWeightedFusion ?? false
    };

    var results = await hybridSearchService.SearchAsync(request.Query, options, cancellationToken);

    Log.Information("RRF search processed: Query='{Query}', Results={ResultCount}", request.Query, results.Count());

    return Results.Ok(new RrfSearchResponse(
        request.Query,
        results.Count(),
        results.Select(r => new RrfSearchResultItem(
            r.Id,
            r.Content,
            r.Score,
            r.Rank,
            r.Source,
            r.Metadata)).ToList()));
});

// API endpoint for searching indexed file paths (OpenClaw integration)
app.MapPost("/api/search/paths", async (PathSearchRequest request, IFileMetadataQueryService queryService, CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(request.Pattern))
        return Results.BadRequest(new { error = "Pattern is required" });

    var limit = request.Limit ?? 50;
    if (limit < 1 || limit > 200)
        return Results.BadRequest(new { error = "Limit must be between 1 and 200" });

    var results = await queryService.SearchFilesAsync(request.Pattern, request.BackupSourceId, limit, cancellationToken);

    Log.Information("Path search processed: Pattern='{Pattern}', Results={ResultCount}", request.Pattern, results.Count);

    return Results.Ok(new PathSearchResponse(
        request.Pattern,
        results.Count,
        results.Select(r => new PathSearchResultItem(
            r.Id,
            r.Path,
            r.Hash,
            r.Size,
            r.LastModified,
            r.BackupSourceId,
            r.BackupSourceName,
            r.IsIndexed,
            r.VersionAdded)).ToList()));
});

app.Run();

// Request DTO for creating a BackupSource
public record CreateBackupSourceRequest(
    Guid? Id,
    string Name,
    string DuplicatiBackupId,
    DateTimeOffset? CreatedAt,
    DateTimeOffset? LastParsedVersion,
    string? EncryptionPassword,
    string? TargetUrl
);

// Request DTO for RAG queries
public record RagQueryRequest(
    string Query,
    int? TopK,
    Guid? SessionId
);

// Response DTO for RAG queries
public record RagQueryResponse(
    Guid SessionId,
    string Answer,
    bool IsNewSession
);

public record HistoryItemResponse(
    Guid Id,
    string OriginalQuery,
    string CondensedQuery,
    string Response,
    DateTimeOffset QueryTimestamp,
    DateTimeOffset ResponseTimestamp,
    List<DuplicatiIndexer.Data.Entities.QueryHistoryEvent>? Events
);

// Response DTO for session details
public record SessionDetailsResponse(
    Guid Id,
    string Title,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastActivityAt,
    IReadOnlyList<HistoryItemResponse> History
);

// Response DTO for file history
public record FileHistoryResponse(
    string Path,
    IReadOnlyList<FileVersionHistoryItem> Versions
);

// Response DTO for file search
public record FileSearchResponse(
    string Pattern,
    int TotalCount,
    IReadOnlyList<FileMetadataResult> Files
);

// Response DTO for file listing
public record FileListResponse(
    string Directory,
    bool Recursive,
    int TotalCount,
    IReadOnlyList<FileMetadataResult> Files
);

// Response DTO for files modified between dates
public record FilesModifiedResponse(
    DateTimeOffset StartDate,
    DateTimeOffset EndDate,
    int TotalCount,
    IReadOnlyList<FileMetadataResult> Files
);

// Request DTO for ChromaDB test insert
public record TestChromaInsertRequest(
    Guid? FileId,
    string? Content,
    int? VectorSize
);

// Request DTO for RRF hybrid search
public record RrfSearchRequest(
    string Query,
    int? TopKPerMethod,
    int? FinalTopK,
    int? RrfK,
    double? VectorWeight,
    double? SparseWeight,
    bool? UseWeightedFusion
);

// Response DTO for RRF search result item
public record RrfSearchResultItem(
    string Id,
    string Content,
    double Score,
    int Rank,
    string Source,
    Dictionary<string, object> Metadata
);

// Response DTO for RRF search
public record RrfSearchResponse(
    string Query,
    int TotalCount,
    IReadOnlyList<RrfSearchResultItem> Results
);

// Request DTO for path search
public record PathSearchRequest(
    string Pattern,
    Guid? BackupSourceId,
    int? Limit
);

// Response DTO for path search result item
public record PathSearchResultItem(
    Guid Id,
    string Path,
    string Hash,
    long Size,
    DateTimeOffset LastModified,
    Guid BackupSourceId,
    string BackupSourceName,
    bool IsIndexed,
    DateTimeOffset VersionAdded
);

// Response DTO for path search
public record PathSearchResponse(
    string Pattern,
    int TotalCount,
    IReadOnlyList<PathSearchResultItem> Results
);
