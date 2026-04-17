using DuplicatiIndexer.AdapterInterfaces;
using DuplicatiIndexer.Configuration;
using DuplicatiIndexer.Data.Entities;
using Marten;
using Npgsql;

namespace DuplicatiIndexer.Services;

/// <summary>
/// PostgreSQL-based implementation of sparse (full-text) indexing using tsvector.
/// </summary>
public class PostgreSqlSparseIndex : ISparseIndex
{
    private readonly IDocumentSession _documentSession;
    private readonly ILogger<PostgreSqlSparseIndex> _logger;
    private readonly string _connectionString;

    /// <summary>
    /// Initializes a new instance of the <see cref="PostgreSqlSparseIndex"/> class.
    /// </summary>
    /// <param name="documentSession">The Marten document session.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="config">The environment configuration.</param>
    public PostgreSqlSparseIndex(
        IDocumentSession documentSession,
        ILogger<PostgreSqlSparseIndex> logger,
        EnvironmentConfig config)
    {
        _documentSession = documentSession;
        _logger = logger;
        _connectionString = config.ConnectionStrings.DocumentStore;
        if (string.IsNullOrEmpty(_connectionString))
        {
            logger.LogError("PostgreSQL connection string not found in configuration");
            throw new InvalidOperationException("PostgreSQL connection string not found");
        }
    }

    /// <inheritdoc />
    public async Task IndexContentAsync(Guid fileId, string content, IReadOnlyList<string> groupIds, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Indexing content for file {FileId} in PostgreSQL full-text index with groups {Groups}", fileId, string.Join(", ", groupIds));

        try
        {
            // Delete existing content for this file
            await DeleteFileContentAsync(fileId, cancellationToken);

            // Create new indexed content
            var indexedContent = new IndexedContent
            {
                Id = fileId,
                FileEntryId = fileId,
                Content = content,
                IndexedAt = DateTimeOffset.UtcNow,
                GroupIds = groupIds.ToList()
            };

            // Store using Marten (tsvector will be computed by PostgreSQL)
            _documentSession.Store(indexedContent);
            await _documentSession.SaveChangesAsync(cancellationToken);

            _logger.LogInformation("Successfully indexed content for file {FileId}", fileId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to index content for file {FileId}", fileId);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task IndexChunkAsync(Guid fileId, int chunkIndex, string content, IReadOnlyList<string> groupIds, CancellationToken cancellationToken = default)
    {
        var chunkId = DeterministicGuid(fileId, chunkIndex);
        _logger.LogDebug("Indexing chunk {ChunkId} for file {FileId} (chunk {ChunkIndex}) with groups {Groups}", chunkId, fileId, chunkIndex, string.Join(", ", groupIds));

        try
        {
            // Use a single atomic operation via SQL UPSERT to avoid race conditions
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            const string upsertSql = @"
                INSERT INTO indexed_content (id, data)
                VALUES (
                    @Id,
                    jsonb_build_object(
                        'Id', @Id,
                        'FileEntryId', @FileEntryId,
                        'ChunkIndex', @ChunkIndex,
                        'Content', @Content,
                        'IndexedAt', @IndexedAt,
                        'GroupIds', @GroupIds::jsonb
                    )
                )
                ON CONFLICT (id) DO UPDATE SET
                    data = EXCLUDED.data";

            await using var command = new NpgsqlCommand(upsertSql, connection);
            command.Parameters.AddWithValue("@Id", chunkId);
            command.Parameters.AddWithValue("@FileEntryId", fileId.ToString());
            command.Parameters.AddWithValue("@ChunkIndex", chunkIndex);
            command.Parameters.AddWithValue("@Content", content);
            command.Parameters.AddWithValue("@IndexedAt", DateTimeOffset.UtcNow);
            command.Parameters.AddWithValue("@GroupIds", System.Text.Json.JsonSerializer.Serialize(groupIds));

            await command.ExecuteNonQueryAsync(cancellationToken);

            _logger.LogDebug("Successfully indexed chunk {ChunkId} for file {FileId}", chunkId, fileId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to index chunk {ChunkId} for file {FileId}", chunkId, fileId);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task DeleteFileContentAsync(Guid fileId, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Deleting indexed content for file {FileId}", fileId);

        try
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            // Delete all content for this file (both chunks and main content) in a single transaction
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            try
            {
                // Delete all chunks for this file
                const string deleteChunksSql = @"
                    DELETE FROM indexed_content
                    WHERE data @> jsonb_build_object('FileEntryId', @FileId::text)
                    AND COALESCE((data->>'ChunkIndex')::int, -1) >= 0"; 

                await using var deleteChunksCmd = new NpgsqlCommand(deleteChunksSql, connection, transaction);
                deleteChunksCmd.Parameters.AddWithValue("@FileId", fileId.ToString());
                var deletedChunks = await deleteChunksCmd.ExecuteNonQueryAsync(cancellationToken);
                _logger.LogDebug("Deleted {Count} chunks for file {FileId}", deletedChunks, fileId);

                // Delete the main file content
                const string deleteMainSql = @"
                    DELETE FROM indexed_content
                    WHERE id = @FileId";

                await using var deleteMainCmd = new NpgsqlCommand(deleteMainSql, connection, transaction);
                deleteMainCmd.Parameters.AddWithValue("@FileId", fileId);
                var deletedMain = await deleteMainCmd.ExecuteNonQueryAsync(cancellationToken);

                await transaction.CommitAsync(cancellationToken);

                _logger.LogInformation("Successfully deleted indexed content for file {FileId} ({Chunks} chunks, {Main} main)", fileId, deletedChunks, deletedMain);
            }
            catch
            {
                await transaction.RollbackAsync(cancellationToken);
                throw;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete indexed content for file {FileId}", fileId);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<IEnumerable<SearchResult>> SearchAsync(string query, IReadOnlyList<string> allowedGroupIds, int topK = 5, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Performing full-text search for: {Query} with allowed groups {Groups}", query, string.Join(", ", allowedGroupIds));

        var results = new List<SearchResult>();

        try
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            // Build the group filter condition
            var groupFilter = BuildGroupFilter(allowedGroupIds);

            string sql = @"
                SELECT
                    id,
                    data->>'Content' as content,
                    data->>'FileEntryId' as file_entry_id,
                    COALESCE((data->>'ChunkIndex')::int, -1) as chunk_index,
                    ts_rank_cd(
                        to_tsvector('english', data->>'Content'),
                        plainto_tsquery('english', @Query),
                        32
                    ) as rank
                FROM indexed_content
                WHERE to_tsvector('english', data->>'Content') @@ plainto_tsquery('english', @Query)
                AND (" + groupFilter + @")
                ORDER BY rank DESC
                LIMIT @Limit";

            await using var command = new NpgsqlCommand(sql, connection);
            command.Parameters.AddWithValue("@Query", query);
            command.Parameters.AddWithValue("@Limit", topK);

            // Add group filter parameters
            for (int i = 0; i < allowedGroupIds.Count; i++)
            {
                command.Parameters.AddWithValue($"@Group{i}", allowedGroupIds[i]);
            }

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            int rank = 1;

            while (await reader.ReadAsync(cancellationToken))
            {
                var id = reader.GetGuid(0);
                var content = reader.GetString(1);
                var fileEntryId = Guid.Parse(reader.GetString(2));
                var chunkIndex = reader.GetInt32(3);
                var score = reader.GetDouble(4);

                results.Add(new SearchResult
                {
                    Id = id.ToString(),
                    Content = content,
                    Score = score,
                    Rank = rank++,
                    Source = "sparse",
                    Metadata = new Dictionary<string, object>
                    {
                        ["file_id"] = fileEntryId.ToString(),
                        ["chunk_index"] = chunkIndex,
                        ["is_chunk"] = chunkIndex >= 0
                    }
                });
            }

            _logger.LogInformation("Full-text search found {Count} results for: {Query}", results.Count, query);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Full-text search failed for query: {Query}", query);
            throw;
        }

        return results;
    }

    /// <summary>
    /// Builds a SQL filter condition for group IDs using JSONB containment.
    /// </summary>
    private static string BuildGroupFilter(IReadOnlyList<string> allowedGroupIds)
    {
        if (allowedGroupIds.Count == 0)
        {
            return "1=0"; // No groups allowed, return no results
        }

        var conditions = new List<string>();
        for (int i = 0; i < allowedGroupIds.Count; i++)
        {
            conditions.Add($"data->'GroupIds' @> to_jsonb(@Group{i}::text)");
        }

        return string.Join(" OR ", conditions);
    }

    /// <summary>
    /// Generates a deterministic GUID from a file ID and chunk index.
    /// </summary>
    private static Guid DeterministicGuid(Guid fileId, int chunkIndex)
    {
        var inputBytes = System.Text.Encoding.UTF8.GetBytes($"{fileId:N}_{chunkIndex}");
        var hashBytes = System.Security.Cryptography.SHA256.HashData(inputBytes);

        var guidBytes = new byte[16];
        Array.Copy(hashBytes, guidBytes, 16);

        guidBytes[6] = (byte)((guidBytes[6] & 0x0F) | 0x50);
        guidBytes[8] = (byte)((guidBytes[8] & 0x3F) | 0x80);

        return new Guid(guidBytes);
    }
}
