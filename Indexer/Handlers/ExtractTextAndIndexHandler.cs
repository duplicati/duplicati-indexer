using DuplicatiIndexer.AdapterInterfaces;
using DuplicatiIndexer.Data.Entities;
using DuplicatiIndexer.Messages;
using Marten;
using Wolverine.Attributes;

namespace DuplicatiIndexer.Handlers;

/// <summary>
/// Handler for processing ExtractTextAndIndex messages.
/// Extracts text from restored files using Unstructured and indexes them into the vector database.
/// </summary>
[MessageTimeout(5 * 60)] // 5 minutes timeout for embedding operations
public class ExtractTextAndIndexHandler
{
    private readonly IContentIndexer _contentIndexer;
    private readonly IEmbeddingService _embeddingService;
    private readonly IVectorStore _vectorStore;
    private readonly ISparseIndex _sparseIndex;
    private readonly ITextChunker _textChunker;
    private readonly IDocumentSession _session;
    private readonly IGroupPermissionService _groupPermissionService;
    private readonly ILogger<ExtractTextAndIndexHandler> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ExtractTextAndIndexHandler"/> class.
    /// </summary>
    /// <param name="contentIndexer">The content indexer for text extraction.</param>
    /// <param name="embeddingService">The embedding service for generating embeddings.</param>
    /// <param name="vectorStore">The vector store for storing embeddings.</param>
    /// <param name="sparseIndex">The sparse index for full-text search.</param>
    /// <param name="textChunker">The text chunker for splitting large texts.</param>
    /// <param name="session">The Marten document session.</param>
    /// <param name="groupPermissionService">The group permission service for ACL.</param>
    /// <param name="logger">The logger.</param>
    public ExtractTextAndIndexHandler(
        IContentIndexer contentIndexer,
        IEmbeddingService embeddingService,
        IVectorStore vectorStore,
        ISparseIndex sparseIndex,
        ITextChunker textChunker,
        IDocumentSession session,
        IGroupPermissionService groupPermissionService,
        ILogger<ExtractTextAndIndexHandler> logger)
    {
        _contentIndexer = contentIndexer;
        _embeddingService = embeddingService;
        _vectorStore = vectorStore;
        _sparseIndex = sparseIndex;
        _textChunker = textChunker;
        _session = session;
        _groupPermissionService = groupPermissionService;
        _logger = logger;
    }

    /// <summary>
    /// Handles an ExtractTextAndIndex message by extracting text and indexing it into the vector database.
    /// </summary>
    /// <param name="message">The ExtractTextAndIndex message.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    [WolverineHandler]
    public async Task Handle(ExtractTextAndIndex message, CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Received ExtractTextAndIndex message for BackupId: {BackupId}, FileEntryId: {FileEntryId}, Path: {OriginalFilePath}",
            message.BackupId, message.FileEntryId, message.OriginalFilePath);

        try
        {
            // Verify the file exists
            if (!File.Exists(message.RestoredFilePath))
            {
                _logger.LogError(
                    "Restored file not found at {RestoredFilePath} for FileEntryId: {FileEntryId}",
                    message.RestoredFilePath, message.FileEntryId);
                throw new FileNotFoundException(
                    "Restored file not found", message.RestoredFilePath);
            }

            // Get the file entry from database to verify it exists
            var fileEntry = await _session.LoadAsync<BackupFileEntry>(message.FileEntryId, cancellationToken);
            if (fileEntry == null)
            {
                _logger.LogError(
                    "FileEntry not found in database for FileEntryId: {FileEntryId}",
                    message.FileEntryId);
                throw new InvalidOperationException(
                    $"FileEntry not found for FileEntryId: {message.FileEntryId}");
            }

            // Get the allowed group IDs for this file using the backup source's default groups
            var groupIds = _groupPermissionService.GetAllowedGroupIds(message.OriginalFilePath, message.DefaultGroupIds);
            _logger.LogInformation(
                "File {FilePath} is accessible by groups: {Groups} (default groups from backup: {DefaultGroups})",
                message.OriginalFilePath, string.Join(", ", groupIds), string.Join(", ", message.DefaultGroupIds));

            _logger.LogInformation(
                "Extracting text from {RestoredFilePath} using Unstructured",
                message.RestoredFilePath);

            // Step 1: Extract text using Unstructured
            string extractedText;
            try
            {
                extractedText = await _contentIndexer.ExtractTextAsync(
                    message.RestoredFilePath, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Failed to extract text from {RestoredFilePath} for FileEntryId: {FileEntryId}",
                    message.RestoredFilePath, message.FileEntryId);
                // Mark as failed but don't re-throw to allow other files to be processed
                await UpdateIndexingStatusAsync(fileEntry, FileIndexingStatus.Failed, ex.Message, cancellationToken);
                return;
            }

            if (string.IsNullOrWhiteSpace(extractedText))
            {
                _logger.LogWarning(
                    "No text extracted from {RestoredFilePath} for FileEntryId: {FileEntryId}. Skipping vector indexing.",
                    message.RestoredFilePath, message.FileEntryId);
                await UpdateIndexingStatusAsync(fileEntry, FileIndexingStatus.NoContent, null, cancellationToken);
                return;
            }

            _logger.LogInformation(
                "Extracted {TextLength} characters from {RestoredFilePath}. Chunking text (max chunk size: {MaxChunkSize})...",
                extractedText.Length, message.RestoredFilePath, _textChunker.MaxChunkSize);

            // Step 2: Chunk the text
            IReadOnlyList<TextChunk> chunks;
            try
            {
                chunks = await _textChunker.ChunkTextAsync(extractedText, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Failed to chunk text for FileEntryId: {FileEntryId}",
                    message.FileEntryId);
                await UpdateIndexingStatusAsync(fileEntry, FileIndexingStatus.Failed, ex.Message, cancellationToken);
                return;
            }

            _logger.LogInformation(
                "Text split into {ChunkCount} chunks for FileEntryId: {FileEntryId}. Generating embeddings...",
                chunks.Count, message.FileEntryId);

            // Step 3: Delete old chunks for this file before storing new ones (both vector and sparse)
            try
            {
                await _vectorStore.DeleteFileChunksAsync(message.FileEntryId, cancellationToken);
                await _sparseIndex.DeleteFileContentAsync(message.FileEntryId, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Failed to delete old chunks for FileEntryId: {FileEntryId}. Continuing with upsert...",
                    message.FileEntryId);
            }

            // Step 4: Generate embeddings and store chunks (both vector and sparse)
            // Both indexes must succeed for a chunk to be considered successfully indexed
            var embeddingDimension = 0;
            var successfulChunks = 0;
            var failedChunks = 0;

            for (int i = 0; i < chunks.Count; i++)
            {
                var chunk = chunks[i];
                cancellationToken.ThrowIfCancellationRequested();

                // Track if both indexing operations succeed for this chunk
                bool vectorIndexed = false;
                bool sparseIndexed = false;
                Exception? vectorException = null;
                Exception? sparseException = null;

                // Generate embedding for this chunk (required for vector indexing)
                float[]? embedding = null;
                try
                {
                    embedding = await _embeddingService.GenerateEmbeddingAsync(chunk.Content, cancellationToken);
                    embeddingDimension = embedding.Length;

                    // Ensure collection exists on first successful embedding
                    if (i == 0)
                    {
                        await _vectorStore.EnsureCollectionExistsAsync(embedding.Length, cancellationToken);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "Failed to generate embedding for chunk {ChunkIndex}/{TotalChunks} for FileEntryId: {FileEntryId}",
                        chunk.Index + 1, chunk.TotalChunks, message.FileEntryId);
                    failedChunks++;
                    continue; // Skip to next chunk if we can't even generate the embedding
                }

                // Vector indexing
                try
                {
                    await _vectorStore.UpsertChunkVectorAsync(
                        message.FileEntryId,
                        chunk.Index,
                        chunk.Content,
                        embedding!,
                        groupIds,
                        cancellationToken);

                    vectorIndexed = true;
                }
                catch (Exception ex)
                {
                    vectorException = ex;
                    _logger.LogError(ex,
                        "Failed to store vector chunk {ChunkIndex}/{TotalChunks} for FileEntryId: {FileEntryId}",
                        chunk.Index + 1, chunk.TotalChunks, message.FileEntryId);
                }

                // Sparse indexing (full-text)
                try
                {
                    await _sparseIndex.IndexChunkAsync(
                        message.FileEntryId,
                        chunk.Index,
                        chunk.Content,
                        groupIds,
                        cancellationToken);

                    sparseIndexed = true;
                }
                catch (Exception ex)
                {
                    sparseException = ex;
                    _logger.LogError(ex,
                        "Failed to store sparse chunk {ChunkIndex}/{TotalChunks} for FileEntryId: {FileEntryId}",
                        chunk.Index + 1, chunk.TotalChunks, message.FileEntryId);
                }

                // Only count as successful if BOTH indexes succeeded
                if (vectorIndexed && sparseIndexed)
                {
                    successfulChunks++;
                    _logger.LogDebug(
                        "Successfully indexed chunk {ChunkIndex}/{TotalChunks} (vector + sparse) for FileEntryId: {FileEntryId}",
                        chunk.Index + 1, chunk.TotalChunks, message.FileEntryId);
                }
                else
                {
                    failedChunks++;

                    // Attempt to clean up partial index to maintain consistency
                    try
                    {
                        if (vectorIndexed && !sparseIndexed)
                        {
                            // Vector succeeded but sparse failed - clean up vector
                            _logger.LogWarning(
                                "Vector indexed but sparse failed for chunk {ChunkIndex}. Attempting cleanup...",
                                chunk.Index);
                            // Note: Vector store doesn't have chunk-level delete,
                            // but the file-level delete at the start of re-indexing will handle this
                        }
                        else if (!vectorIndexed && sparseIndexed)
                        {
                            // Sparse succeeded but vector failed - clean up sparse
                            _logger.LogWarning(
                                "Sparse indexed but vector failed for chunk {ChunkIndex}. Attempting cleanup...",
                                chunk.Index);
                            // We can't easily delete a single chunk, but the file-level delete
                            // at the start of re-indexing will handle cleanup on retry
                        }
                    }
                    catch (Exception cleanupEx)
                    {
                        _logger.LogWarning(cleanupEx,
                            "Failed to clean up partial index for chunk {ChunkIndex}", chunk.Index);
                    }
                }
            }

            // Check if at least some chunks were successfully processed
            if (successfulChunks == 0)
            {
                var errorMessage = failedChunks > 0
                    ? $"All {failedChunks} chunks failed to index"
                    : "All chunks failed to process";
                _logger.LogError(
                    "Failed to index any chunks for FileEntryId: {FileEntryId} ({FailedChunks} failed)",
                    message.FileEntryId, failedChunks);
                await UpdateIndexingStatusAsync(fileEntry, FileIndexingStatus.Failed, errorMessage, cancellationToken);
                return;
            }

            // Log warning if some chunks failed (partial success)
            if (failedChunks > 0)
            {
                _logger.LogWarning(
                    "Partial indexing for FileEntryId: {FileEntryId} - {SuccessfulChunks}/{TotalChunks} chunks succeeded, {FailedChunks} failed",
                    message.FileEntryId, successfulChunks, chunks.Count, failedChunks);
            }

            _logger.LogInformation(
                "Successfully indexed {SuccessfulChunks}/{TotalChunks} chunks (embedding dimension: {EmbeddingDimension}) for FileEntryId: {FileEntryId}",
                successfulChunks, chunks.Count, embeddingDimension, message.FileEntryId);

            // Step 5: Update the file entry status
            await UpdateIndexingStatusAsync(fileEntry, FileIndexingStatus.Indexed, null, cancellationToken);

            _logger.LogInformation(
                "Successfully indexed FileEntryId: {FileEntryId}, Path: {OriginalFilePath} into vector database",
                message.FileEntryId, message.OriginalFilePath);

            // Clean up the restored file after successful indexing
            try
            {
                File.Delete(message.RestoredFilePath);
                _logger.LogDebug(
                    "Cleaned up restored file: {RestoredFilePath}",
                    message.RestoredFilePath);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Failed to clean up restored file: {RestoredFilePath}",
                    message.RestoredFilePath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Error processing ExtractTextAndIndex message for FileEntryId: {FileEntryId}, Path: {OriginalFilePath}",
                message.FileEntryId, message.OriginalFilePath);
            throw;
        }
    }

    /// <summary>
    /// Updates the indexing status of a file entry.
    /// </summary>
    private async Task UpdateIndexingStatusAsync(
        BackupFileEntry fileEntry,
        FileIndexingStatus status,
        string? errorMessage,
        CancellationToken cancellationToken)
    {
        fileEntry.IndexingStatus = status;
        fileEntry.LastIndexingAttempt = DateTimeOffset.UtcNow;
        fileEntry.IndexingErrorMessage = errorMessage;

        _session.Store(fileEntry);

        try
        {
            await _session.SaveChangesAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // If the token is already canceled, try saving without cancellation
            // to ensure the error status is persisted
            _logger.LogDebug(
                "Cancellation token was triggered, saving status update without cancellation token");
            await _session.SaveChangesAsync(CancellationToken.None);
        }

        _logger.LogDebug(
            "Updated indexing status for FileEntryId: {FileEntryId} to {Status}",
            fileEntry.Id, status);
    }
}
