using DuplicatiIndexer.AdapterInterfaces;
using DuplicatiIndexer.Data;
using DuplicatiIndexer.Data.Entities;

namespace DuplicatiIndexer.Services;

/// <summary>
/// Service that combines dense vector search and sparse full-text search
/// using Reciprocal Rank Fusion (RRF) to provide hybrid search capabilities.
/// </summary>
public class HybridSearchService : IHybridSearchService
{
    private readonly IVectorStore _vectorStore;
    private readonly ISparseIndex _sparseIndex;
    private readonly IEmbeddingService _embeddingService;
    private readonly ISurrealRepository _repository;
    private readonly ILogger<HybridSearchService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="HybridSearchService"/> class.
    /// </summary>
    public HybridSearchService(
        IVectorStore vectorStore,
        ISparseIndex sparseIndex,
        IEmbeddingService embeddingService,
        ISurrealRepository repository,
        ILogger<HybridSearchService> logger)
    {
        _vectorStore = vectorStore;
        _sparseIndex = sparseIndex;
        _embeddingService = embeddingService;
        _repository = repository;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<IEnumerable<SearchResult>> SearchAsync(
        string query,
        HybridSearchOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new HybridSearchOptions();

        _logger.LogInformation(
            "Performing hybrid search for: {Query} (vector_weight={VectorWeight}, sparse_weight={SparseWeight})",
            query, options.VectorWeight, options.SparseWeight);

        // Generate embedding for the query
        var queryEmbedding = await _embeddingService.GenerateEmbeddingAsync(query, cancellationToken);

        return await SearchAsync(query, queryEmbedding, options, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IEnumerable<SearchResult>> SearchAsync(
        string queryText,
        float[] queryVector,
        HybridSearchOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new HybridSearchOptions();

        _logger.LogInformation(
            "Performing hybrid search with pre-computed embedding for: {Query} (topK={TopK}, rrf_k={RrfK})",
            queryText, options.FinalTopK, options.RrfK);

        // Execute both searches in parallel
        var vectorSearchTask = _vectorStore.SearchAsync(queryVector, options.TopKPerMethod, cancellationToken);
        var sparseSearchTask = _sparseIndex.SearchAsync(queryText, options.TopKPerMethod, cancellationToken);

        await Task.WhenAll(vectorSearchTask, sparseSearchTask);

        var vectorResults = await vectorSearchTask;
        var sparseResults = await sparseSearchTask;

        _logger.LogDebug(
            "Vector search returned {VectorCount} results, sparse search returned {SparseCount} results",
            vectorResults.Count(), sparseResults.Count());

        // Create result sets for RRF
        var vectorResultSet = new SearchResultSet
        {
            SourceName = "vector",
            Query = queryText,
            Results = vectorResults.ToList()
        };

        var sparseResultSet = new SearchResultSet
        {
            SourceName = "sparse",
            Query = queryText,
            Results = sparseResults.ToList()
        };

        // Perform RRF fusion
        List<SearchResult> fusedResults;
        if (options.UseWeightedFusion)
        {
            fusedResults = RRF.FuseWeighted(
                vectorResultSet, options.VectorWeight,
                sparseResultSet, options.SparseWeight,
                options.RrfK);
        }
        else
        {
            fusedResults = RRF.Fuse(new[] { vectorResultSet, sparseResultSet }, options.RrfK);
        }

        // Take top K results
        var finalResults = fusedResults.Take(options.FinalTopK).ToList();

        _logger.LogInformation(
            "Hybrid search completed with {FusedCount} fused results, returning top {FinalCount}",
            fusedResults.Count, finalResults.Count);

        // Log result sources for debugging
        foreach (var result in finalResults)
        {
            if (result.Metadata.TryGetValue("sources", out var sources))
            {
                _logger.LogDebug(
                    "Result {Id} found in sources: {Sources} (RRF score: {Score})",
                    result.Id, string.Join(", ", (List<string>)sources), result.Score);
            }
        }

        // Hydrate Filename and FileType
        var uniqueFileIds = finalResults
            .Where(r => r.Metadata.ContainsKey("FileId") && !string.IsNullOrEmpty(r.Metadata["FileId"]?.ToString()))
            .Select(r => r.Metadata["FileId"]!.ToString()!)
            .Distinct()
            .ToList();

        if (uniqueFileIds.Any())
        {
            var tasks = uniqueFileIds.Select(id => _repository.GetAsync<BackupFileEntry>(Guid.Parse(id), cancellationToken));
            var fileEntries = await Task.WhenAll(tasks);
            var fileMap = fileEntries.Where(e => e != null).ToDictionary(e => e!.Id.ToString());

            foreach (var result in finalResults)
            {
                if (result.Metadata.TryGetValue("FileId", out var fileIdObj) && fileIdObj is string fileIdStr && fileMap.TryGetValue(fileIdStr, out var entry))
                {
                    result.Metadata["Filename"] = Path.GetFileName(entry.Path);
                    result.Metadata["FileType"] = entry.FileType;
                    result.Metadata["Path"] = entry.Path;
                }
            }
        }

        return finalResults;
    }

    /// <inheritdoc />
    public async Task<IEnumerable<SearchResult>> SearchVectorAsync(
        string query,
        int topK = 5,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Performing vector search for: {Query}", query);

        var queryEmbedding = await _embeddingService.GenerateEmbeddingAsync(query, cancellationToken);
        var results = await _vectorStore.SearchAsync(queryEmbedding, topK, cancellationToken);

        return results;
    }

    /// <inheritdoc />
    public async Task<IEnumerable<SearchResult>> SearchSparseAsync(
        string query,
        int topK = 5,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Performing sparse search for: {Query}", query);

        var results = await _sparseIndex.SearchAsync(query, topK, cancellationToken);
        return results;
    }
}
