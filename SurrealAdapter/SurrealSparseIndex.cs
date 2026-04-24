using DuplicatiIndexer.AdapterInterfaces;
using Microsoft.Extensions.Logging;
using SurrealDb.Net;
using System.Text.Json;

namespace DuplicatiIndexer.SurrealAdapter;

public class SurrealSparseIndex : ISparseIndex
{
    private readonly ISurrealDbClient _client;
    private readonly ILogger<SurrealSparseIndex> _logger;
    private const string TableName = "sparse_chunk";

    public SurrealSparseIndex(ISurrealDbClient client, ILogger<SurrealSparseIndex> logger)
    {
        _client = client;
        _logger = logger;
    }

    private static bool _hasEnsuredIndex = false;
    private static readonly SemaphoreSlim _initSemaphore = new SemaphoreSlim(1, 1);

    public async Task EnsureIndexExistsAsync(CancellationToken cancellationToken = default)
    {
        if (_hasEnsuredIndex) return;
        
        await _initSemaphore.WaitAsync(cancellationToken);
        try
        {
            if (_hasEnsuredIndex) return;

            // During bulk ingestion, only create the table schema + FileId index.
            // The expensive BM25 search index is deferred to BuildSearchIndexAsync()
            // to avoid per-write tokenization + index maintenance overhead.
            await _client.RawQuery($@"
                DEFINE TABLE {TableName} SCHEMALESS;
                DEFINE INDEX fileid_idx ON {TableName} FIELDS FileId;
            ");
            _logger.LogInformation("Ensured sparse table {TableName} exists (BM25 index deferred for bulk perf)", TableName);
            
            _hasEnsuredIndex = true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to ensure sparse index exists");
        }
        finally
        {
            _initSemaphore.Release();
        }
    }

    /// <summary>
    /// Creates the BM25 full-text search index after bulk ingestion is complete.
    /// Building the index once over the full dataset is orders of magnitude faster
    /// than maintaining it incrementally during writes.
    /// </summary>
    public async Task BuildSearchIndexAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Building BM25 sparse search index on {TableName}...", TableName);
        await _client.RawQuery($@"
            DEFINE ANALYZER sparse_analyzer TOKENIZERS blank,class,camel,punct FILTERS lowercase,snowball(english);
            DEFINE INDEX sparse_idx ON {TableName} FIELDS Content SEARCH ANALYZER sparse_analyzer BM25;
        ");
        _logger.LogInformation("BM25 sparse search index built successfully on {TableName}", TableName);
    }

    public async Task IndexContentAsync(Guid fileId, string content, CancellationToken cancellationToken = default)
    {
        await IndexChunkAsync(fileId, 0, content, cancellationToken);
    }

    public async Task IndexChunkAsync(Guid fileId, int chunkIndex, string content, CancellationToken cancellationToken = default)
    {
        var properties = new Dictionary<string, object>
        {
            { "FileId", fileId.ToString() },
            { "ChunkIndex", chunkIndex },
            { "Content", content }
        };

        var id = $"{fileId:N}_{chunkIndex}";
        await _client.Query($"UPSERT type::thing({TableName}, {id}) CONTENT {properties}", cancellationToken);
    }

    public async Task IndexChunksBatchAsync(Guid fileId, IReadOnlyList<(int Index, string Content)> chunks, CancellationToken cancellationToken = default)
    {
        if (chunks.Count == 0) return;

        var tasks = chunks.Select(chunk => 
            IndexChunkAsync(fileId, chunk.Index, chunk.Content, cancellationToken)
        );

        await Task.WhenAll(tasks);
    }

    public async Task IndexCrossFileChunksBatchAsync(IEnumerable<(Guid FileId, int Index, string Content)> chunks, CancellationToken cancellationToken = default)
    {
        var records = chunks.Select(c => new
        {
            id = $"{c.FileId:N}_{c.Index}",
            FileId = c.FileId.ToString(),
            ChunkIndex = c.Index,
            Content = c.Content
        }).ToList();

        if (records.Count == 0) return;

        string queryStr = $"INSERT INTO {TableName} $records ON DUPLICATE KEY UPDATE FileId=$input.FileId, ChunkIndex=$input.ChunkIndex, Content=$input.Content;";
        await _client.RawQuery(queryStr, new Dictionary<string, object?> { { "records", records } }, cancellationToken);
    }

    public async Task DeleteFileContentAsync(Guid fileId, CancellationToken cancellationToken = default)
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

    public async Task<IEnumerable<SearchResult>> SearchAsync(string query, int topK = 5, CancellationToken cancellationToken = default)
    {
        var parameters = new Dictionary<string, object?> { { "q", query } };
        var response = await _client.RawQuery($@"
            SELECT 
                type::string(id) AS id,
                type::string(FileId) AS FileId,
                ChunkIndex,
                Content,
                search::score(1) AS score
            FROM {TableName}
            WHERE Content @1@ $q
            ORDER BY score DESC
            LIMIT {topK};
        ", parameters);
        var results = response.GetValue<List<RawSearchResult>>(0);
        
        if (results == null) return Array.Empty<SearchResult>();

        return results.Select((r, i) => new SearchResult
        {
            Id = r.id ?? "",
            Content = r.Content ?? "",
            Score = r.score,
            Rank = i + 1,
            Source = "sparse",
            Metadata = new Dictionary<string, object>
            {
                { "FileId", r.FileId ?? "" },
                { "ChunkIndex", r.ChunkIndex }
            }
        }).ToList();
    }
}
