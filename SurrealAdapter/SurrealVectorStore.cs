using DuplicatiIndexer.AdapterInterfaces;
using Microsoft.Extensions.Logging;
using SurrealDb.Net;
using System.Text.Json;

namespace DuplicatiIndexer.SurrealAdapter;

public class SurrealVectorStore : IVectorStore
{
    private readonly ISurrealDbClient _client;
    private readonly ILogger<SurrealVectorStore> _logger;
    private const string TableName = "vector_chunk";

    public SurrealVectorStore(ISurrealDbClient client, ILogger<SurrealVectorStore> logger)
    {
        _client = client;
        _logger = logger;
    }

    private static bool _hasEnsuredCollection = false;
    private static readonly SemaphoreSlim _initSemaphore = new SemaphoreSlim(1, 1);

    private int _vectorSize;

    public async Task EnsureCollectionExistsAsync(int vectorSize, CancellationToken cancellationToken = default)
    {
        if (_hasEnsuredCollection) return;

        await _initSemaphore.WaitAsync(cancellationToken);
        try
        {
            if (_hasEnsuredCollection) return;
            _vectorSize = vectorSize;

            // During bulk ingestion, only create the table schema + FileId index.
            // The expensive HNSW search index is deferred to BuildSearchIndexAsync()
            // to avoid per-write index maintenance overhead (saves 300-800ms per UPSERT).
            await _client.RawQuery($@"
                DEFINE TABLE {TableName} SCHEMALESS;
                DEFINE FIELD Vector ON {TableName} TYPE array<float>;
                DEFINE INDEX fileid_idx ON {TableName} FIELDS FileId;
            ");
            _logger.LogInformation("Ensured vector collection {TableName} exists (search index deferred for bulk perf)", TableName);
            
            _hasEnsuredCollection = true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to ensure vector collection exists");
        }
        finally
        {
            _initSemaphore.Release();
        }
    }

    /// <summary>
    /// Creates the HNSW search index after bulk ingestion is complete.
    /// Building the index once over the full dataset is orders of magnitude faster
    /// than maintaining it incrementally during writes.
    /// </summary>
    public async Task BuildSearchIndexAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Building HNSW vector search index on {TableName} (dimension: {VectorSize})...", TableName, _vectorSize);
        await _client.RawQuery($@"
            DEFINE INDEX vector_idx ON {TableName} FIELDS Vector HNSW DIMENSION {_vectorSize} DIST COSINE M 16 EFC 40;
        ");
        _logger.LogInformation("HNSW vector search index built successfully on {TableName}", TableName);
    }

    public async Task UpsertVectorAsync(Guid fileId, string content, float[] vector, CancellationToken cancellationToken = default)
    {
        await UpsertChunkVectorAsync(fileId, 0, content, vector, cancellationToken);
    }

    public async Task UpsertChunkVectorAsync(Guid fileId, int chunkIndex, string content, float[] vector, CancellationToken cancellationToken = default)
    {
        var properties = new Dictionary<string, object>
        {
            { "FileId", fileId.ToString() },
            { "ChunkIndex", chunkIndex },
            { "Content", content },
            { "Vector", vector }
        };

        // Create deterministic ID
        var id = $"{fileId:N}_{chunkIndex}";
        
        await _client.Query($"UPSERT type::thing({TableName}, {id}) CONTENT {properties}", cancellationToken);
    }

    public async Task UpsertChunkVectorsBatchAsync(Guid fileId, IReadOnlyList<(int Index, string Content, float[] Vector)> chunks, CancellationToken cancellationToken = default)
    {
        if (chunks.Count == 0) return;

        // Bypassing massive native index-locking loops within Surreal's INSERT ... ON DUPLICATE KEY UPDATE
        // by executing perfectly isolated highly-concurrent native UPSERT type::thing queries directly.
        var tasks = chunks.Select(chunk => 
            UpsertChunkVectorAsync(fileId, chunk.Index, chunk.Content, chunk.Vector, cancellationToken)
        );

        await Task.WhenAll(tasks);
    }

    public async Task UpsertCrossFileChunkVectorsBatchAsync(IEnumerable<(Guid FileId, int Index, string Content, float[] Vector)> chunks, CancellationToken cancellationToken = default)
    {
        var records = chunks.Select(c => new
        {
            id = $"{c.FileId:N}_{c.Index}",
            FileId = c.FileId.ToString(),
            ChunkIndex = c.Index,
            Content = c.Content,
            Vector = c.Vector
        }).ToList();

        if (records.Count == 0) return;

        // Execute a SINGLE massive bulk insert command instead of 100 concurrent HTTP proxy locks
        string queryStr = $"INSERT INTO {TableName} $records ON DUPLICATE KEY UPDATE FileId=$input.FileId, ChunkIndex=$input.ChunkIndex, Content=$input.Content, Vector=$input.Vector;";
        await _client.RawQuery(queryStr, new Dictionary<string, object?> { { "records", records } }, cancellationToken);
    }

    public async Task DeleteFileChunksAsync(Guid fileId, CancellationToken cancellationToken = default)
    {
        await _client.RawQuery($"DELETE {TableName} WHERE FileId = '{fileId}'");
    }

    public class RawSearchResult
    {
        public string? id { get; set; }
        public string? FileId { get; set; }
        public int ChunkIndex { get; set; }
        public string? Content { get; set; }
        public double score { get; set; }
    }

    public async Task<IEnumerable<SearchResult>> SearchAsync(float[] queryVector, int topK = 5, CancellationToken cancellationToken = default)
    {
        var vectorStr = $"[{string.Join(", ", queryVector)}]";
        var response = await _client.RawQuery($@"
            SELECT 
                type::string(id) AS id, 
                type::string(FileId) AS FileId, 
                ChunkIndex, 
                Content, 
                vector::similarity::cosine(Vector, {vectorStr}) AS score 
            FROM {TableName} 
            WHERE Vector <|{topK}|> {vectorStr}
            ORDER BY score DESC;
        ");
        var results = response.GetValue<List<RawSearchResult>>(0);
        
        if (results == null) return Array.Empty<SearchResult>();

        return results.Select((r, i) => new SearchResult
        {
            Id = r.id ?? "",
            Content = r.Content ?? "",
            Score = r.score,
            Rank = i + 1,
            Source = "vector",
            Metadata = new Dictionary<string, object>
            {
                { "FileId", r.FileId ?? "" },
                { "ChunkIndex", r.ChunkIndex }
            }
        }).ToList();
    }

    public async Task<IEnumerable<string>> SearchContentAsync(float[] queryVector, int topK = 5, CancellationToken cancellationToken = default)
    {
        var results = await SearchAsync(queryVector, topK, cancellationToken);
        return results.Select(r => r.Content).ToList();
    }
}
