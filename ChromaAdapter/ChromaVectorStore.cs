using DuplicatiIndexer.AdapterInterfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Net.Http.Json;
using System.Text;

namespace DuplicatiIndexer.ChromaAdapter;

/// <summary>
/// ChromaDB implementation using direct HTTP calls to v2 API.
/// Bypasses the outdated ChromaDB.Client library which only supports v1.
/// </summary>
public class ChromaVectorStore : IVectorStore
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<ChromaVectorStore> _logger;
    private readonly string _baseUrl;
    private readonly string _collectionName;
    private readonly string _tenant;
    private readonly string _database;
    private string? _collectionId;

    public ChromaVectorStore(HttpClient httpClient, ILogger<ChromaVectorStore> logger, IConfiguration configuration)
    {
        _httpClient = httpClient;
        _logger = logger;

        var connectionString = configuration["ConnectionStrings:Chroma"] ?? "http://localhost:8000";
        // Ensure no trailing slash and no /api/v1 suffix
        _baseUrl = connectionString.TrimEnd('/').Replace("/api/v1", "");
        _collectionName = configuration["VectorStore:CollectionName"] ?? "backup_content";
        _tenant = "default_tenant";
        _database = "default_database";

        _logger.LogInformation("ChromaVectorStore initialized with URL: {BaseUrl}, Collection: {Collection}",
            _baseUrl, _collectionName);
    }

    public async Task EnsureCollectionExistsAsync(int vectorSize, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Ensuring Chroma collection {Collection} exists", _collectionName);

        try
        {
            // Check if collection exists
            var collection = await GetCollectionAsync(cancellationToken);

            if (collection == null)
            {
                _logger.LogInformation("Creating Chroma collection: {Collection}", _collectionName);
                await CreateCollectionAsync(cancellationToken);
            }
            else
            {
                _collectionId = collection.Id;
                _logger.LogInformation("Found existing Chroma collection: {Collection} (ID: {Id})",
                    _collectionName, _collectionId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to ensure Chroma collection {Collection} exists", _collectionName);
            throw;
        }
    }

    public async Task UpsertVectorAsync(Guid fileId, string content, float[] vector, IReadOnlyList<string> groupIds, CancellationToken cancellationToken = default)
    {
        await EnsureCollectionInitializedAsync(cancellationToken);

        _logger.LogInformation("Upserting vector for file {FileId} into Chroma collection {Collection} with groups {Groups}",
            fileId, _collectionName, string.Join(", ", groupIds));

        var request = new
        {
            ids = new[] { fileId.ToString() },
            embeddings = new[] { vector },
            documents = new[] { content },
            metadatas = new[]
            {
                new Dictionary<string, object>
                {
                    ["file_id"] = fileId.ToString(),
                    ["group_ids"] = groupIds.ToList()
                }
            }
        };

        var response = await _httpClient.PostAsJsonAsync(
            $"{_baseUrl}/api/v2/tenants/{_tenant}/databases/{_database}/collections/{_collectionId}/upsert",
            request, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException($"Failed to upsert vector: {response.StatusCode} - {error}");
        }

        _logger.LogInformation("Successfully upserted vector for file {FileId}", fileId);
    }

    public async Task UpsertChunkVectorAsync(Guid fileId, int chunkIndex, string content, float[] vector, IReadOnlyList<string> groupIds, CancellationToken cancellationToken = default)
    {
        await EnsureCollectionInitializedAsync(cancellationToken);

        var chunkId = DeterministicGuid(fileId, chunkIndex);
        _logger.LogDebug("Upserting chunk vector {ChunkId} for file {FileId} (chunk {ChunkIndex}) with groups {Groups}",
            chunkId, fileId, chunkIndex, string.Join(", ", groupIds));

        var request = new
        {
            ids = new[] { chunkId.ToString() },
            embeddings = new[] { vector },
            documents = new[] { content },
            metadatas = new[]
            {
                new Dictionary<string, object>
                {
                    ["file_id"] = fileId.ToString(),
                    ["chunk_index"] = chunkIndex,
                    ["is_chunk"] = true,
                    ["group_ids"] = groupIds.ToList()
                }
            }
        };

        var response = await _httpClient.PostAsJsonAsync(
            $"{_baseUrl}/api/v2/tenants/{_tenant}/databases/{_database}/collections/{_collectionId}/upsert",
            request, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException($"Failed to upsert chunk vector: {response.StatusCode} - {error}");
        }

        _logger.LogDebug("Successfully upserted chunk vector {ChunkId} for file {FileId}", chunkId, fileId);
    }

    public async Task<IEnumerable<SearchResult>> SearchAsync(float[] queryVector, IReadOnlyList<string> allowedGroupIds, int topK = 5, CancellationToken cancellationToken = default)
    {
        await EnsureCollectionInitializedAsync(cancellationToken);

        _logger.LogInformation("Searching Chroma collection {Collection} for top {TopK} matches with allowed groups {Groups}", _collectionName, topK, string.Join(", ", allowedGroupIds));

        // Build where clause for group filtering using $in operator
        var whereClause = BuildGroupFilter(allowedGroupIds);

        var request = new
        {
            query_embeddings = new[] { queryVector },
            n_results = topK,
            include = new[] { "metadatas", "documents", "distances" },
            where = whereClause
        };

        var response = await _httpClient.PostAsJsonAsync(
            $"{_baseUrl}/api/v2/tenants/{_tenant}/databases/{_database}/collections/{_collectionId}/query",
            request, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException($"Failed to search: {response.StatusCode} - {error}");
        }

        var result = await response.Content.ReadFromJsonAsync<QueryResponse>(cancellationToken);
        var searchResults = new List<SearchResult>();

        if (result?.Ids?.Count > 0)
        {
            int rank = 1;
            for (int i = 0; i < result.Ids[0].Count; i++)
            {
                var metadata = result.Metadatas?[0]?[i] ?? new Dictionary<string, object>();

                searchResults.Add(new SearchResult
                {
                    Id = result.Ids[0][i],
                    Content = result.Documents?[0]?[i] ?? string.Empty,
                    Score = 1.0 - (result.Distances?[0]?[i] ?? 0),
                    Rank = rank++,
                    Source = "vector",
                    Metadata = new Dictionary<string, object>
                    {
                        ["file_id"] = metadata.TryGetValue("file_id", out var fileIdValue) ? fileIdValue?.ToString() ?? string.Empty : string.Empty,
                        ["chunk_index"] = metadata.TryGetValue("chunk_index", out var chunkIdx) ? chunkIdx : -1,
                        ["is_chunk"] = metadata.TryGetValue("is_chunk", out var isChunk) && isChunk?.ToString() == "true"
                    }
                });
            }
        }

        _logger.LogInformation("Found {Count} matches in Chroma", searchResults.Count);
        return searchResults.Where(r => !string.IsNullOrEmpty(r.Content));
    }

    public async Task<IEnumerable<string>> SearchContentAsync(float[] queryVector, IReadOnlyList<string> allowedGroupIds, int topK = 5, CancellationToken cancellationToken = default)
    {
        var results = await SearchAsync(queryVector, allowedGroupIds, topK, cancellationToken);
        return results.Select(r => r.Content);
    }

    public async Task DeleteFileChunksAsync(Guid fileId, CancellationToken cancellationToken = default)
    {
        await EnsureCollectionInitializedAsync(cancellationToken);

        _logger.LogInformation("Deleting chunk vectors for file {FileId}", fileId);

        var request = new
        {
            where = new Dictionary<string, object>
            {
                ["file_id"] = fileId.ToString(),
                ["is_chunk"] = true
            }
        };

        var response = await _httpClient.PostAsJsonAsync(
            $"{_baseUrl}/api/v2/tenants/{_tenant}/databases/{_database}/collections/{_collectionId}/get",
            request, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogDebug("Could not get chunks for file {FileId}, assuming none exist", fileId);
            return;
        }

        var result = await response.Content.ReadFromJsonAsync<GetResponse>(cancellationToken);
        var ids = result?.Ids ?? new List<string>();

        if (ids.Count > 0)
        {
            var deleteRequest = new { ids = ids };
            var deleteResponse = await _httpClient.PostAsJsonAsync(
                $"{_baseUrl}/api/v2/tenants/{_tenant}/databases/{_database}/collections/{_collectionId}/delete",
                deleteRequest, cancellationToken);

            if (!deleteResponse.IsSuccessStatusCode)
            {
                var error = await deleteResponse.Content.ReadAsStringAsync(cancellationToken);
                throw new HttpRequestException($"Failed to delete chunks: {deleteResponse.StatusCode} - {error}");
            }

            _logger.LogInformation("Deleted {Count} chunk vectors for file {FileId}", ids.Count, fileId);
        }
    }

    private async Task EnsureCollectionInitializedAsync(CancellationToken cancellationToken)
    {
        if (_collectionId == null)
        {
            var collection = await GetCollectionAsync(cancellationToken);
            if (collection == null)
            {
                throw new InvalidOperationException($"Collection {_collectionName} does not exist");
            }
            _collectionId = collection.Id;
        }
    }

    private async Task<CollectionInfo?> GetCollectionAsync(CancellationToken cancellationToken)
    {
        try
        {
            var response = await _httpClient.GetAsync(
                $"{_baseUrl}/api/v2/tenants/{_tenant}/databases/{_database}/collections",
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Failed to list collections: {StatusCode}", response.StatusCode);
                return null;
            }

            var collections = await response.Content.ReadFromJsonAsync<List<CollectionInfo>>(cancellationToken);
            return collections?.FirstOrDefault(c => c.Name == _collectionName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting collection {Collection}", _collectionName);
            return null;
        }
    }

    private async Task CreateCollectionAsync(CancellationToken cancellationToken)
    {
        var request = new
        {
            name = _collectionName,
            configuration = new
            {
                hnsw_configuration = new
                {
                    space = "cosine"
                }
            },
            metadata = new Dictionary<string, object>
            {
                ["created"] = DateTimeOffset.UtcNow.ToString("O"),
                ["source"] = "duplicati-indexer"
            }
        };

        var response = await _httpClient.PostAsJsonAsync(
            $"{_baseUrl}/api/v2/tenants/{_tenant}/databases/{_database}/collections",
            request, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException($"Failed to create collection: {response.StatusCode} - {error}");
        }

        var collection = await response.Content.ReadFromJsonAsync<CollectionInfo>(cancellationToken);
        _collectionId = collection?.Id;

        _logger.LogInformation("Created Chroma collection: {Collection} (ID: {Id})", _collectionName, _collectionId);
    }

    /// <summary>
    /// Builds a ChromaDB where clause for filtering by allowed group IDs.
    /// Uses the $in operator to match any of the allowed groups.
    /// If no groups are allowed, returns a filter that matches nothing.
    /// </summary>
    private static Dictionary<string, object>? BuildGroupFilter(IReadOnlyList<string> allowedGroupIds)
    {
        // No groups allowed = no access (security: deny by default)
        if (allowedGroupIds.Count == 0)
        {
            // Return a filter that matches nothing (group_ids doesn't contain empty string)
            return new Dictionary<string, object>
            {
                ["group_ids"] = new Dictionary<string, object>
                {
                    ["$in"] = new List<string>()
                }
            };
        }

        // ChromaDB uses $in operator for array containment check
        // The format is: { "group_ids": { "$in": ["group1", "group2"] } }
        return new Dictionary<string, object>
        {
            ["group_ids"] = new Dictionary<string, object>
            {
                ["$in"] = allowedGroupIds.ToList()
            }
        };
    }

    private static Guid DeterministicGuid(Guid fileId, int chunkIndex)
    {
        var inputBytes = Encoding.UTF8.GetBytes($"{fileId:N}_{chunkIndex}");
        var hashBytes = System.Security.Cryptography.SHA256.HashData(inputBytes);
        var guidBytes = new byte[16];
        Array.Copy(hashBytes, guidBytes, 16);
        guidBytes[6] = (byte)((guidBytes[6] & 0x0F) | 0x50);
        guidBytes[8] = (byte)((guidBytes[8] & 0x3F) | 0x80);
        return new Guid(guidBytes);
    }
}

// DTOs for ChromaDB v2 API responses
public class CollectionInfo
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
}

public class QueryResponse
{
    public List<List<string>> Ids { get; set; } = new();
    public List<List<List<float>>>? Embeddings { get; set; }
    public List<List<string>>? Documents { get; set; }
    public List<List<Dictionary<string, object>>>? Metadatas { get; set; }
    public List<List<double>>? Distances { get; set; }
}

public class GetResponse
{
    public List<string> Ids { get; set; } = new();
    public List<List<float>>? Embeddings { get; set; }
    public List<string>? Documents { get; set; }
    public List<Dictionary<string, object>>? Metadatas { get; set; }
}
