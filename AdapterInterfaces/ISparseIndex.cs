namespace DuplicatiIndexer.AdapterInterfaces;

/// <summary>
/// Service for storing and searching content using sparse (full-text) indexing.
/// Provides keyword-based search capabilities to complement dense vector search.
/// </summary>
public interface ISparseIndex
{
    /// <summary>
    /// Ensures that the required sparse search indexes exist for the storage provider natively.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    Task EnsureIndexExistsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Builds the search index after bulk ingestion is complete.
    /// </summary>
    Task BuildSearchIndexAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Indexes content for a file entry in the sparse index.
    /// </summary>
    /// <param name="fileId">The unique identifier of the file.</param>
    /// <param name="content">The text content to index.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    Task IndexContentAsync(Guid fileId, string content, CancellationToken cancellationToken = default);

    /// <summary>
    /// Indexes content for a file chunk in the sparse index.
    /// </summary>
    /// <param name="fileId">The unique identifier of the file.</param>
    /// <param name="chunkIndex">The index of the chunk within the file.</param>
    /// <param name="content">The text content to index.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    Task IndexChunkAsync(Guid fileId, int chunkIndex, string content, CancellationToken cancellationToken = default);

    /// <summary>
    /// Indexes multiple chunks for a file in a single batch.
    /// </summary>
    /// <param name="fileId">The unique identifier of the file.</param>
    /// <param name="chunks">A list of tuples containing chunk index and content.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    Task IndexChunksBatchAsync(Guid fileId, IReadOnlyList<(int Index, string Content)> chunks, CancellationToken cancellationToken = default);

    Task IndexCrossFileChunksBatchAsync(IEnumerable<(Guid FileId, int Index, string Content)> chunks, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes all indexed content for a file.
    /// </summary>
    /// <param name="fileId">The unique identifier of the file.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    Task DeleteFileContentAsync(Guid fileId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Performs a full-text search using the provided query.
    /// </summary>
    /// <param name="query">The search query string.</param>
    /// <param name="topK">The maximum number of results to return.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A collection of search results with scores.</returns>
    Task<IEnumerable<SearchResult>> SearchAsync(string query, int topK = 5, CancellationToken cancellationToken = default);
}
