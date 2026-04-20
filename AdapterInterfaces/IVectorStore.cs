namespace DuplicatiIndexer.AdapterInterfaces;

/// <summary>
/// Service for storing and searching vector embeddings.
/// </summary>
public interface IVectorStore
{
    /// <summary>
    /// Ensures the collection exists in the vector store.
    /// </summary>
    /// <param name="vectorSize">The size of the embedding vectors.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    Task EnsureCollectionExistsAsync(int vectorSize, CancellationToken cancellationToken = default);

    /// <summary>
    /// Builds the search index after bulk ingestion is complete.
    /// </summary>
    Task BuildSearchIndexAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Stores or updates a vector embedding for a file.
    /// </summary>
    /// <param name="fileId">The unique identifier of the file.</param>
    /// <param name="content">The text content associated with the vector.</param>
    /// <param name="vector">The embedding vector.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    Task UpsertVectorAsync(Guid fileId, string content, float[] vector, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stores or updates a vector embedding chunk for a file.
    /// </summary>
    /// <param name="fileId">The unique identifier of the file.</param>
    /// <param name="chunkIndex">The index of the chunk.</param>
    /// <param name="content">The text content associated with the vector.</param>
    /// <param name="vector">The embedding vector.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    Task UpsertChunkVectorAsync(Guid fileId, int chunkIndex, string content, float[] vector, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stores or updates multiple vector embedding chunks for a file in a single batch.
    /// </summary>
    /// <param name="fileId">The unique identifier of the file.</param>
    /// <param name="chunks">A list of tuples containing chunk index, content, and the vector.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    Task UpsertChunkVectorsBatchAsync(Guid fileId, IReadOnlyList<(int Index, string Content, float[] Vector)> chunks, CancellationToken cancellationToken = default);

    Task UpsertCrossFileChunkVectorsBatchAsync(IEnumerable<(Guid FileId, int Index, string Content, float[] Vector)> chunks, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes all chunk vectors for a file. Used when re-indexing a file with different chunks.
    /// </summary>
    /// <param name="fileId">The unique identifier of the file.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    Task DeleteFileChunksAsync(Guid fileId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Searches for similar content using a query vector.
    /// Returns scored results suitable for RRF fusion.
    /// </summary>
    /// <param name="queryVector">The query embedding vector.</param>
    /// <param name="topK">The maximum number of results to return.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A collection of search results with scores and metadata.</returns>
    Task<IEnumerable<SearchResult>> SearchAsync(float[] queryVector, int topK = 5, CancellationToken cancellationToken = default);

    /// <summary>
    /// Searches for similar content using a query vector and returns only content strings.
    /// Legacy method for backward compatibility.
    /// </summary>
    /// <param name="queryVector">The query embedding vector.</param>
    /// <param name="topK">The maximum number of results to return.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A collection of matching content strings.</returns>
    Task<IEnumerable<string>> SearchContentAsync(float[] queryVector, int topK = 5, CancellationToken cancellationToken = default);
}
