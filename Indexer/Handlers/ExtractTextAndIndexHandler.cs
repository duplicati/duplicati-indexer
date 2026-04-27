using DuplicatiIndexer.AdapterInterfaces;
using DuplicatiIndexer.Messages;
using Wolverine;
using Wolverine.Attributes;
using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DuplicatiIndexer.Data.Entities;
using DuplicatiIndexer.Data;
using DuplicatiIndexer.Services;
using DuplicatiIndexer.Configuration;
namespace DuplicatiIndexer.Handlers;

public class ExtractTextAndIndexHandler
{
    private readonly IContentIndexer _contentIndexer;
    private readonly IEmbeddingService _embeddingService;
    private readonly IVectorStore _vectorStore;
    private readonly ISparseIndex _sparseIndex;
    private readonly ITextChunker _textChunker;
    private readonly ISurrealRepository _repository;
    private readonly ILogger<ExtractTextAndIndexHandler> _logger;
    private readonly DbStatsLiveMonitor _statsLiveMonitor;
    private readonly bool _isIncrementalRun;

    public ExtractTextAndIndexHandler(
        IContentIndexer contentIndexer,
        IEmbeddingService embeddingService,
        IVectorStore vectorStore,
        ISparseIndex sparseIndex,
        ITextChunker textChunker,
        ISurrealRepository repository,
        ILogger<ExtractTextAndIndexHandler> logger,
        DbStatsLiveMonitor statsLiveMonitor,
        EnvironmentConfig environmentConfig)
    {
        _contentIndexer = contentIndexer;
        _embeddingService = embeddingService;
        _vectorStore = vectorStore;
        _sparseIndex = sparseIndex;
        _textChunker = textChunker;
        _repository = repository;
        _logger = logger;
        _statsLiveMonitor = statsLiveMonitor;
        _isIncrementalRun = environmentConfig.Indexing.IsIncrementalRun;
    }

    [WolverineHandler]
    public async Task Handle(ExtractTextAndIndex[] messages, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Processing batch of {Count} text extraction and index messages natively natively", messages.Length);

        var extractedFiles = new List<(BackupFileEntry Entry, IReadOnlyList<TextChunk> Chunks, string RestoredPath, string FullText)>();

        foreach (var message in messages)
        {
            try
            {
                if (!File.Exists(message.RestoredFilePath)) continue;

                BackupFileEntry? fileEntry = null;
                if (message.FileEntryId.HasValue && message.FileEntryId.Value != Guid.Empty)
                    fileEntry = await _repository.GetAsync<BackupFileEntry>(message.FileEntryId.Value, cancellationToken);
                else
                {
                    var fileEntries = await _repository.QueryAsync<BackupFileEntry>(
                        "SELECT * FROM backupfileentry WHERE BackupSourceId = $backupSourceId AND Path = $originalFilePath LIMIT 1",
                        new Dictionary<string, object> { { "backupSourceId", message.BackupSourceId }, { "originalFilePath", message.OriginalFilePath } }, cancellationToken);
                    fileEntry = fileEntries.FirstOrDefault();
                }

                if (fileEntry == null) continue;

                string extractedText = await _contentIndexer.ExtractTextAsync(message.RestoredFilePath, cancellationToken);
                
                if (string.IsNullOrWhiteSpace(extractedText))
                {
                    await UpdateIndexingStatusAsync(fileEntry, FileIndexingStatus.NoContent, null, cancellationToken);
                    File.Delete(message.RestoredFilePath);
                    continue;
                }

                var chunks = await _textChunker.ChunkTextAsync(extractedText, cancellationToken);
                _statsLiveMonitor.IncrementExtractedChunk(chunks.Count);

                extractedFiles.Add((fileEntry, chunks, message.RestoredFilePath, extractedText));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to prep file {Path}", message.OriginalFilePath);
                try { File.Delete(message.RestoredFilePath); } catch { }
            }
        }

        if (!extractedFiles.Any()) return;

        var allChunks = new List<(Guid FileId, int Index, string Content)>();
        foreach (var file in extractedFiles)
            foreach (var chunk in file.Chunks)
                allChunks.Add((file.Entry.Id, chunk.Index, chunk.Content));

        if (!allChunks.Any()) return;

        try
        {
            var texts = allChunks.Select(c => c.Content).ToList();
            var embeddings = await _embeddingService.GenerateEmbeddingsAsync(texts, cancellationToken);

            var finalVectorList = new List<(Guid FileId, int Index, string Content, float[] Vector)>();
            for(int i=0; i < allChunks.Count; i++) {
                finalVectorList.Add((allChunks[i].FileId, allChunks[i].Index, allChunks[i].Content, embeddings[i]));
            }

            if (finalVectorList.Any())
                await _vectorStore.EnsureCollectionExistsAsync(finalVectorList.First().Vector.Length, cancellationToken);
            await _sparseIndex.EnsureIndexExistsAsync(cancellationToken);

            var sparseRecords = extractedFiles.Select(f => (f.Entry.Id, 0, f.FullText));
            var vectorTask = _vectorStore.UpsertCrossFileChunkVectorsBatchAsync(finalVectorList, cancellationToken);
            var sparseTask = _sparseIndex.IndexCrossFileChunksBatchAsync(sparseRecords, cancellationToken);
            await Task.WhenAll(vectorTask, sparseTask);

            _statsLiveMonitor.IncrementVector(finalVectorList.Count);
            _statsLiveMonitor.IncrementSparse(extractedFiles.Count);

            foreach (var file in extractedFiles)
            {
                await UpdateIndexingStatusAsync(file.Entry, FileIndexingStatus.Indexed, null, cancellationToken);
                File.Delete(file.RestoredPath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to execute massive batch matrix inference loop array.");
        }
    }

    private async Task UpdateIndexingStatusAsync(BackupFileEntry fileEntry, FileIndexingStatus status, string? errorMessage, CancellationToken cancellationToken)
    {
        fileEntry.IndexingStatus = status;
        fileEntry.LastIndexingAttempt = DateTime.UtcNow;
        fileEntry.IndexingErrorMessage = errorMessage;

        try
        {
            await _repository.QueryAsync<object>(
                "UPDATE type::thing('backupfileentry', $id) SET IndexingStatus = $status, LastIndexingAttempt = $timestamp, IndexingErrorMessage = $errorMessage",
                new Dictionary<string, object>
                {
                    { "id", fileEntry.Id }, { "status", (int)status },
                    { "timestamp", DateTime.UtcNow.ToString("o") }, { "errorMessage", errorMessage ?? "" }
                }, cancellationToken);
            _statsLiveMonitor.IncrementIndexedFile(1);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed status update.");
        }
    }
}
