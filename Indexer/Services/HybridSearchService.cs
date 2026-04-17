using DuplicatiIndexer.AdapterInterfaces;

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
    private readonly ILogger<HybridSearchService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="HybridSearchService"/> class.
    /// </summary>
    /// <param name="vectorStore">The vector store for dense search.</param>
    /// <param name="sparseIndex">The sparse index for full-text search.</param>
    /// <param name="embeddingService">The embedding service for generating query embeddings.</param>
    /// <param name="logger">The logger.</param>
    public HybridSearchService(
        IVectorStore vectorStore,
        ISparseIndex sparseIndex,
        IEmbeddingService embeddingService,
        ILogger<HybridSearchService> logger)
    {
        _vectorStore = vectorStore;
        _sparseIndex = sparseIndex;
        _embeddingService = embeddingService;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<IEnumerable<SearchResult>> SearchAsync(
        string query,
        IReadOnlyList<string> allowedGroupIds,
        HybridSearchOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new HybridSearchOptions();

        _logger.LogInformation(
            "Performing hybrid search for: {Query} with allowed groups {Groups} (vector_weight={VectorWeight}, sparse_weight={SparseWeight})",
            query, string.Join(", ", allowedGroupIds), options.VectorWeight, options.SparseWeight);

        // Generate embedding for the query
        var queryEmbedding = await _embeddingService.GenerateEmbeddingAsync(query, cancellationToken);

        return await SearchAsync(query, queryEmbedding, allowedGroupIds, options, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IEnumerable<SearchResult>> SearchAsync(
        string queryText,
        float[] queryVector,
        IReadOnlyList<string> allowedGroupIds,
        HybridSearchOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new HybridSearchOptions();

        _logger.LogInformation(
            "Performing hybrid search with pre-computed embedding for: {Query} with allowed groups {Groups} (topK={TopK}, rrf_k={RrfK})",
            queryText, string.Join(", ", allowedGroupIds), options.FinalTopK, options.RrfK);

        // Execute both searches in parallel with group filtering
        var vectorSearchTask = _vectorStore.SearchAsync(queryVector, allowedGroupIds, options.TopKPerMethod, cancellationToken);
        var sparseSearchTask = _sparseIndex.SearchAsync(queryText, allowedGroupIds, options.TopKPerMethod, cancellationToken);

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

        return finalResults;
    }

    /// <inheritdoc />
    public async Task<IEnumerable<SearchResult>> SearchVectorAsync(
        string query,
        IReadOnlyList<string> allowedGroupIds,
        int topK = 5,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Performing vector search for: {Query} with allowed groups {Groups}", query, string.Join(", ", allowedGroupIds));

        var queryEmbedding = await _embeddingService.GenerateEmbeddingAsync(query, cancellationToken);
        var results = await _vectorStore.SearchAsync(queryEmbedding, allowedGroupIds, topK, cancellationToken);

        return results;
    }

    /// <inheritdoc />
    public async Task<IEnumerable<SearchResult>> SearchSparseAsync(
        string query,
        IReadOnlyList<string> allowedGroupIds,
        int topK = 5,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Performing sparse search for: {Query} with allowed groups {Groups}", query, string.Join(", ", allowedGroupIds));

        var results = await _sparseIndex.SearchAsync(query, allowedGroupIds, topK, cancellationToken);
        return results;
    }
}
