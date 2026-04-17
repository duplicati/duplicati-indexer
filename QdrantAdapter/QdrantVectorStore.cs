using DuplicatiIndexer.AdapterInterfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Qdrant.Client;
using Qdrant.Client.Grpc;

namespace DuplicatiIndexer.QdrantAdapter;

/// <summary>
/// Qdrant implementation of the vector store interface.
/// </summary>
public class QdrantVectorStore : IVectorStore
{
    private readonly QdrantClient _client;
    private readonly ILogger<QdrantVectorStore> _logger;
    private readonly string _collectionName;

    /// <summary>
    /// Initializes a new instance of the <see cref="QdrantVectorStore"/> class.
    /// </summary>
    /// <param name="client">The Qdrant client.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="configuration">The configuration.</param>
    public QdrantVectorStore(QdrantClient client, ILogger<QdrantVectorStore> logger, IConfiguration configuration)
    {
        _client = client;
        _logger = logger;
        _collectionName = configuration["VectorStore:CollectionName"] ?? "backup_content";
    }

    /// <inheritdoc />
    public async Task EnsureCollectionExistsAsync(int vectorSize, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Ensuring Qdrant collection {Collection} exists with vector size {VectorSize}",
            _collectionName, vectorSize);

        var collections = await _client.ListCollectionsAsync(cancellationToken);
        if (!collections.Contains(_collectionName))
        {
            _logger.LogInformation("Creating Qdrant collection {Collection}", _collectionName);

            await _client.CreateCollectionAsync(
                _collectionName,
                new VectorParams
                {
                    Size = (ulong)vectorSize,
                    Distance = Distance.Cosine
                },
                cancellationToken: cancellationToken);

            _logger.LogInformation("Successfully created Qdrant collection {Collection}", _collectionName);
        }
        else
        {
            _logger.LogDebug("Qdrant collection {Collection} already exists", _collectionName);
        }
    }

    /// <inheritdoc />
    public async Task UpsertVectorAsync(Guid fileId, string content, float[] vector, IReadOnlyList<string> groupIds, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Upserting vector for file {FileId} into Qdrant collection {Collection} with groups {Groups}",
            fileId, _collectionName, string.Join(", ", groupIds));

        var point = new PointStruct
        {
            Id = new PointId { Uuid = fileId.ToString() },
            Vectors = vector,
            Payload =
            {
                ["content"] = content,
                ["file_id"] = fileId.ToString(),
                ["group_ids"] = new Value { ListValue = new ListValue { Values = { groupIds.Select(g => new Value { StringValue = g }) } } }
            }
        };

        await _client.UpsertAsync(_collectionName, [point], cancellationToken: cancellationToken);

        _logger.LogInformation("Successfully upserted vector for file {FileId}", fileId);
    }

    /// <inheritdoc />
    public async Task<IEnumerable<SearchResult>> SearchAsync(float[] queryVector, IReadOnlyList<string> allowedGroupIds, int topK = 5, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Searching Qdrant collection {Collection} for top {TopK} matches with allowed groups {Groups}", _collectionName, topK, string.Join(", ", allowedGroupIds));

        IReadOnlyList<ScoredPoint> searchResult;
        try
        {
            // Build filter for group IDs using OR condition
            var filter = BuildGroupFilter(allowedGroupIds);

            searchResult = await _client.SearchAsync(
                collectionName: _collectionName,
                vector: queryVector,
                filter: filter,
                limit: (ulong)topK,
                cancellationToken: cancellationToken
            );
        }
        catch (Grpc.Core.RpcException ex) when (ex.StatusCode == Grpc.Core.StatusCode.NotFound)
        {
            _logger.LogWarning("Qdrant collection {Collection} does not exist. Returning empty results.", _collectionName);
            return Enumerable.Empty<SearchResult>();
        }

        var results = searchResult
            .Select((p, index) =>
            {
                var id = p.Id.HasUuid ? p.Id.Uuid : p.Id.Num.ToString();
                var content = p.Payload.TryGetValue("content", out var value) ? value.StringValue : string.Empty;
                var score = p.Score;

                return new SearchResult
                {
                    Id = id,
                    Content = content,
                    Score = score,
                    Rank = index + 1,
                    Source = "vector",
                    Metadata = new Dictionary<string, object>
                    {
                        ["file_id"] = p.Payload.TryGetValue("file_id", out var fileId) ? fileId.StringValue : string.Empty,
                        ["chunk_index"] = p.Payload.TryGetValue("chunk_index", out var chunkIdx) ? chunkIdx.IntegerValue : -1,
                        ["is_chunk"] = p.Payload.TryGetValue("is_chunk", out var isChunk) ? isChunk.ToString() == "true" : false
                    }
                };
            })
            .Where(r => !string.IsNullOrEmpty(r.Content))
            .ToList();

        _logger.LogInformation("Found {Count} matches in Qdrant", results.Count);

        return results;
    }

    /// <inheritdoc />
    public async Task<IEnumerable<string>> SearchContentAsync(float[] queryVector, IReadOnlyList<string> allowedGroupIds, int topK = 5, CancellationToken cancellationToken = default)
    {
        var results = await SearchAsync(queryVector, allowedGroupIds, topK, cancellationToken);
        return results.Select(r => r.Content);
    }

    /// <inheritdoc />
    public async Task UpsertChunkVectorAsync(Guid fileId, int chunkIndex, string content, float[] vector, IReadOnlyList<string> groupIds, CancellationToken cancellationToken = default)
    {
        // Generate a deterministic UUID v5 from fileId + chunkIndex
        // Using DNS namespace UUID combined with our unique identifier
        var chunkId = DeterministicGuid(fileId, chunkIndex);
        _logger.LogDebug("Upserting chunk vector {ChunkId} for file {FileId} (chunk {ChunkIndex}) with groups {Groups}",
            chunkId, fileId, chunkIndex, string.Join(", ", groupIds));

        var point = new PointStruct
        {
            Id = new PointId { Uuid = chunkId.ToString() },
            Vectors = vector,
            Payload =
            {
                ["content"] = content,
                ["file_id"] = fileId.ToString(),
                ["chunk_index"] = chunkIndex,
                ["is_chunk"] = true,
                ["group_ids"] = new Value { ListValue = new ListValue { Values = { groupIds.Select(g => new Value { StringValue = g }) } } }
            }
        };

        await _client.UpsertAsync(_collectionName, [point], cancellationToken: cancellationToken);

        _logger.LogDebug("Successfully upserted chunk vector {ChunkId} for file {FileId}", chunkId, fileId);
    }

    /// <inheritdoc />
    public async Task DeleteFileChunksAsync(Guid fileId, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Deleting all chunk vectors for file {FileId} from Qdrant collection {Collection}",
            fileId, _collectionName);

        // Delete points where file_id equals the fileId and is_chunk is true
        var filter = new Filter
        {
            Must =
            {
                new Condition
                {
                    Field = new FieldCondition
                    {
                        Key = "file_id",
                        Match = new Match { Keyword = fileId.ToString() }
                    }
                },
                new Condition
                {
                    Field = new FieldCondition
                    {
                        Key = "is_chunk",
                        Match = new Match { Boolean = true }
                    }
                }
            }
        };

        await _client.DeleteAsync(_collectionName, filter, cancellationToken: cancellationToken);

        _logger.LogInformation("Successfully deleted chunk vectors for file {FileId}", fileId);
    }

    /// <summary>
    /// Builds a Qdrant filter for matching any of the allowed group IDs.
    /// If no groups are allowed, returns a filter that matches nothing.
    /// </summary>
    private static Filter BuildGroupFilter(IReadOnlyList<string> allowedGroupIds)
    {
        // No groups allowed = no access (security: deny by default)
        if (allowedGroupIds.Count == 0)
        {
            // Return a filter that matches nothing (match a non-existent field with impossible value)
            return new Filter
            {
                Must =
                {
                    new Condition
                    {
                        Field = new FieldCondition
                        {
                            Key = "group_ids",
                            Match = new Match { Keyword = "" }
                        }
                    }
                }
            };
        }

        // Build OR conditions for each group ID
        var conditions = new List<Condition>();
        foreach (var groupId in allowedGroupIds)
        {
            conditions.Add(new Condition
            {
                Field = new FieldCondition
                {
                    Key = "group_ids",
                    Match = new Match { Keyword = groupId }
                }
            });
        }

        return new Filter
        {
            Should = { conditions }
        };
    }

    /// <summary>
    /// Generates a deterministic GUID (UUID v5 style) from a file ID and chunk index.
    /// This ensures the same fileId + chunkIndex always produces the same GUID.
    /// </summary>
    private static Guid DeterministicGuid(Guid fileId, int chunkIndex)
    {
        // Use SHA256 to generate a hash from fileId + chunkIndex
        var inputBytes = System.Text.Encoding.UTF8.GetBytes($"{fileId:N}_{chunkIndex}");
        var hashBytes = System.Security.Cryptography.SHA256.HashData(inputBytes);

        // Take first 16 bytes for the GUID
        var guidBytes = new byte[16];
        Array.Copy(hashBytes, guidBytes, 16);

        // Set version (0101 = version 5) and variant bits (10 = RFC 4122 variant)
        guidBytes[6] = (byte)((guidBytes[6] & 0x0F) | 0x50);
        guidBytes[8] = (byte)((guidBytes[8] & 0x3F) | 0x80);

        return new Guid(guidBytes);
    }
}
