using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SurrealDb.Net;

namespace DuplicatiIndexer.Services;

/// <summary>
/// A persistent background monitor that utilizes SurrealDB's Native WebSockets Live Queries to track exact pipeline status dynamically.
/// </summary>
public class DbStatsLiveMonitor : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<DbStatsLiveMonitor> _logger;

    private long _metadataCount;
    private long _vectorCount;
    private long _sparseCount;
    private long _versionFileCount;
    private long _extractedChunkCount;
    private long _indexedFileCount;

    public long MetadataCount => Interlocked.Read(ref _metadataCount);
    public long VectorCount => Interlocked.Read(ref _vectorCount);
    public long SparseCount => Interlocked.Read(ref _sparseCount);
    public long VersionFileCount => Interlocked.Read(ref _versionFileCount);
    public long ExtractedChunkCount => Interlocked.Read(ref _extractedChunkCount);
    public long IndexedFileCount => Interlocked.Read(ref _indexedFileCount);

    // Event triggered when a live ingestion mutation is observed
    public event Action? OnStatsUpdated;

    public DbStatsLiveMonitor(IServiceProvider serviceProvider, ILogger<DbStatsLiveMonitor> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    private int _baselineInitialized = 0;

    public void InitializeBaselineOnce(long metadataCount, long versionFileCount)
    {
        if (Interlocked.Exchange(ref _baselineInitialized, 1) == 0)
        {
            Interlocked.Add(ref _metadataCount, metadataCount);
            Interlocked.Add(ref _versionFileCount, versionFileCount);
            OnStatsUpdated?.Invoke();
        }
    }

    public void IncrementMetadata(long delta)
    {
        Interlocked.Add(ref _metadataCount, delta);
        OnStatsUpdated?.Invoke();
    }

    public void IncrementVector(long delta)
    {
        Interlocked.Add(ref _vectorCount, delta);
        OnStatsUpdated?.Invoke();
    }

    public void IncrementSparse(long delta)
    {
        Interlocked.Add(ref _sparseCount, delta);
        OnStatsUpdated?.Invoke();
    }

    public void IncrementVersionFile(long delta)
    {
        Interlocked.Add(ref _versionFileCount, delta);
        OnStatsUpdated?.Invoke();
    }

    public void IncrementExtractedChunk(long delta)
    {
        Interlocked.Add(ref _extractedChunkCount, delta);
        OnStatsUpdated?.Invoke();
    }

    public void IncrementIndexedFile(long delta)
    {
        Interlocked.Add(ref _indexedFileCount, delta);
        OnStatsUpdated?.Invoke();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Now fully event-driven, keeping the hosted thread alive for registration but not blocking.
        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(-1, stoppingToken);
        }
    }
}
