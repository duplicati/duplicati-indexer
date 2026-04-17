namespace DuplicatiIndexer.AdapterInterfaces;

/// <summary>
/// Configuration options for hybrid search.
/// </summary>
public class HybridSearchOptions
{
    /// <summary>
    /// The number of results to retrieve from each search method.
    /// </summary>
    public int TopKPerMethod { get; set; } = 10;

    /// <summary>
    /// The final number of results to return after fusion.
    /// </summary>
    public int FinalTopK { get; set; } = 5;

    /// <summary>
    /// The RRF constant k. Higher values reduce rank differences.
    /// Default is 60 as commonly used in literature.
    /// </summary>
    public int RrfK { get; set; } = 60;

    /// <summary>
    /// Weight for vector search results in weighted fusion.
    /// Default is 1.0 for equal weighting.
    /// </summary>
    public double VectorWeight { get; set; } = 1.0;

    /// <summary>
    /// Weight for sparse (full-text) search results in weighted fusion.
    /// Default is 1.0 for equal weighting.
    /// </summary>
    public double SparseWeight { get; set; } = 1.0;

    /// <summary>
    /// Whether to use weighted fusion instead of standard RRF.
    /// </summary>
    public bool UseWeightedFusion { get; set; } = false;
}

/// <summary>
/// Service that combines dense vector search and sparse full-text search
/// using Reciprocal Rank Fusion (RRF) to provide hybrid search capabilities.
/// </summary>
public interface IHybridSearchService
{
    /// <summary>
    /// Performs a hybrid search using both vector similarity and full-text search,
    /// combining results with RRF.
    /// </summary>
    /// <param name="query">The search query text.</param>
    /// <param name="allowedGroupIds">The list of group IDs the user is allowed to access.</param>
    /// <param name="options">Optional search configuration.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A ranked list of search results from both methods combined.</returns>
    Task<IEnumerable<SearchResult>> SearchAsync(
        string query,
        IReadOnlyList<string> allowedGroupIds,
        HybridSearchOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Performs a hybrid search using a pre-computed query vector and the query text.
    /// Useful when the embedding is already available.
    /// </summary>
    /// <param name="queryText">The original search query text for sparse search.</param>
    /// <param name="queryVector">The pre-computed query embedding for vector search.</param>
    /// <param name="allowedGroupIds">The list of group IDs the user is allowed to access.</param>
    /// <param name="options">Optional search configuration.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A ranked list of search results from both methods combined.</returns>
    Task<IEnumerable<SearchResult>> SearchAsync(
        string queryText,
        float[] queryVector,
        IReadOnlyList<string> allowedGroupIds,
        HybridSearchOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets search results from only the vector store.
    /// </summary>
    /// <param name="query">The search query text.</param>
    /// <param name="allowedGroupIds">The list of group IDs the user is allowed to access.</param>
    /// <param name="topK">The number of results to return.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>Vector search results.</returns>
    Task<IEnumerable<SearchResult>> SearchVectorAsync(
        string query,
        IReadOnlyList<string> allowedGroupIds,
        int topK = 5,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets search results from only the sparse index.
    /// </summary>
    /// <param name="query">The search query text.</param>
    /// <param name="allowedGroupIds">The list of group IDs the user is allowed to access.</param>
    /// <param name="topK">The number of results to return.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>Sparse (full-text) search results.</returns>
    Task<IEnumerable<SearchResult>> SearchSparseAsync(
        string query,
        IReadOnlyList<string> allowedGroupIds,
        int topK = 5,
        CancellationToken cancellationToken = default);
}
