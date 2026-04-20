namespace DuplicatiIndexer.AdapterInterfaces;

/// <summary>
/// Service for generating embeddings from text.
/// </summary>
public interface IEmbeddingService
{
    /// <summary>
    /// Generates an embedding vector for the specified text.
    /// </summary>
    /// <param name="text">The text to embed.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A float array representing the embedding vector.</returns>
    Task<float[]> GenerateEmbeddingAsync(string text, CancellationToken cancellationToken = default);

    /// <summary>
    /// Generates embedding vectors for multiple texts in a batch.
    /// </summary>
    /// <param name="texts">The texts to embed.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A list of float arrays representing the embedding vectors.</returns>
    Task<IReadOnlyList<float[]>> GenerateEmbeddingsAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken = default);
}
