using System.Text.RegularExpressions;
using DuplicatiIndexer.Data.Entities;
using DuplicatiIndexer.Data;

namespace DuplicatiIndexer.Services;

/// <summary>
/// Service for querying file metadata from backup entries.
/// </summary>
public interface IFileMetadataQueryService
{
    /// <summary>
    /// Finds the most recent version of a file by path (supports wildcards).
    /// </summary>
    Task<FileMetadataResult?> FindFileAsync(string pathPattern, Guid? backupSourceId = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the version history for a specific file path.
    /// </summary>
    Task<IReadOnlyList<FileVersionHistoryItem>> GetFileVersionHistoryAsync(string path, Guid? backupSourceId = null, int limit = 20, CancellationToken cancellationToken = default);

    /// <summary>
    /// Searches for files by name pattern (supports wildcards).
    /// </summary>
    Task<IReadOnlyList<FileMetadataResult>> SearchFilesAsync(string namePattern, Guid? backupSourceId = null, int limit = 50, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists all files in a specific directory.
    /// </summary>
    Task<IReadOnlyList<FileMetadataResult>> ListDirectoryAsync(string directoryPath, Guid? backupSourceId = null, bool includeSubdirectories = false, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets files that were modified between two dates.
    /// </summary>
    Task<IReadOnlyList<FileMetadataResult>> GetFilesModifiedBetweenAsync(DateTimeOffset startDate, DateTimeOffset endDate, Guid? backupSourceId = null, int limit = 100, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the complete version history for a file from all backup versions.
    /// This includes every backup version where the file appeared, not just when hash changed.
    /// </summary>
    Task<IReadOnlyList<FileVersionInfo>> GetCompleteVersionHistoryAsync(string path, Guid? backupSourceId = null, int limit = 50, CancellationToken cancellationToken = default);

    /// <summary>
    /// Finds when a specific file was last modified by looking at all backup versions.
    /// </summary>
    Task<FileVersionInfo?> GetLastModifiedAsync(string path, Guid? backupSourceId = null, CancellationToken cancellationToken = default);
}

/// <summary>
/// Represents a file version from BackupVersionFile (includes all versions, not just hash changes).
/// </summary>
public class FileVersionInfo
{
    public Guid Id { get; set; }
    public Guid BackupSourceId { get; set; }
    public string Path { get; set; } = string.Empty;
    public string Hash { get; set; } = string.Empty;
    public long Size { get; set; }
    public DateTimeOffset LastModified { get; set; }
    public DateTimeOffset Version { get; set; }
    public DateTimeOffset RecordedAt { get; set; }
}

/// <summary>
/// Result containing file metadata.
/// </summary>
public class FileMetadataResult
{
    public Guid Id { get; set; }
    public Guid BackupSourceId { get; set; }
    public string BackupSourceName { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public string Hash { get; set; } = string.Empty;
    public long Size { get; set; }
    public DateTimeOffset LastModified { get; set; }
    public DateTimeOffset VersionAdded { get; set; }
    public DateTimeOffset? VersionDeleted { get; set; }
    public bool IsDeleted => VersionDeleted.HasValue;
    public FileIndexingStatus IndexingStatus { get; set; }
    public bool IsIndexed => IndexingStatus == FileIndexingStatus.Indexed;
}

/// <summary>
/// Represents a single version of a file in backup history.
/// </summary>
public class FileVersionHistoryItem
{
    public string Hash { get; set; } = string.Empty;
    public long Size { get; set; }
    public DateTimeOffset LastModified { get; set; }
    public DateTimeOffset VersionAdded { get; set; }
    public DateTimeOffset? VersionDeleted { get; set; }
    public bool IsDeleted => VersionDeleted.HasValue;
    public FileIndexingStatus IndexingStatus { get; set; }
}

/// <summary>
/// Implementation of file metadata query service using Marten.
/// </summary>
public class FileMetadataQueryService : IFileMetadataQueryService
{
    private readonly ISurrealRepository _repository;
    private readonly ILogger<FileMetadataQueryService> _logger;

    public FileMetadataQueryService(ISurrealRepository repository, ILogger<FileMetadataQueryService> logger)
    {
        _repository = repository;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<FileMetadataResult?> FindFileAsync(string pathPattern, Guid? backupSourceId = null, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Finding file with pattern '{PathPattern}'", pathPattern);

        var sql = "SELECT * FROM backupfileentry WHERE VersionDeleted = null";
        var parameters = new Dictionary<string, object>();

        if (backupSourceId.HasValue)
        {
            sql += " AND BackupSourceId = $backupSourceId";
            parameters.Add("backupSourceId", backupSourceId.Value);
        }

        IReadOnlyList<BackupFileEntry> candidates;
        if (pathPattern.Contains('*') || pathPattern.Contains('?'))
        {
            sql += " ORDER BY VersionAdded DESC LIMIT 100";
            var candidateList = await _repository.QueryAsync<BackupFileEntry>(sql, parameters, cancellationToken);

            var regex = new Regex(WildcardToRegex(pathPattern), RegexOptions.IgnoreCase);
            candidates = candidateList.Where(f => regex.IsMatch(f.Path)).ToList();
        }
        else
        {
            // Exact match or ends-with match for file names using SurrealDB native functions
            // This prevents the LIMIT 1000 from prematurely truncating the dataset before filtering
            sql += " AND (Path = $pathPattern OR string::ends_with(Path, $pathPattern)) ORDER BY VersionAdded DESC LIMIT 1";
            parameters.Add("pathPattern", pathPattern);
            candidates = await _repository.QueryAsync<BackupFileEntry>(sql, parameters, cancellationToken);
        }

        var entry = candidates.FirstOrDefault();

        if (entry == null)
        {
            _logger.LogWarning("No file found matching pattern '{PathPattern}'", pathPattern);
            return null;
        }

        var backupSource = await _repository.GetAsync<BackupSource>(entry.BackupSourceId, cancellationToken);

        return MapToResult(entry, backupSource);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<FileVersionHistoryItem>> GetFileVersionHistoryAsync(string path, Guid? backupSourceId = null, int limit = 20, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Getting version history for file '{Path}'", path);

        var sql = "SELECT * FROM backupfileentry WHERE Path = $path";
        var parameters = new Dictionary<string, object> { { "path", path } };

        if (backupSourceId.HasValue)
        {
            sql += " AND BackupSourceId = $backupSourceId";
            parameters.Add("backupSourceId", backupSourceId.Value);
        }

        sql += $" ORDER BY VersionAdded DESC LIMIT {limit}";

        var entries = await _repository.QueryAsync<BackupFileEntry>(sql, parameters, cancellationToken);

        return entries.Select(e => new FileVersionHistoryItem
        {
            Hash = e.Hash,
            Size = e.Size,
            LastModified = e.LastModified,
            VersionAdded = e.VersionAdded,
            VersionDeleted = e.VersionDeleted,
            IndexingStatus = e.IndexingStatus
        }).ToList();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<FileMetadataResult>> SearchFilesAsync(string namePattern, Guid? backupSourceId = null, int limit = 50, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Searching files with pattern '{NamePattern}'", namePattern);

        var sql = "SELECT * FROM backupfileentry WHERE VersionDeleted = null";
        var parameters = new Dictionary<string, object>();

        if (backupSourceId.HasValue)
        {
            sql += " AND BackupSourceId = $backupSourceId";
            parameters.Add("backupSourceId", backupSourceId.Value);
        }

        IReadOnlyList<BackupFileEntry> entries;

        if (namePattern.Contains('*') || namePattern.Contains('?'))
        {
            sql += $" ORDER BY Path ASC LIMIT {limit * 2}";
            var entryList = await _repository.QueryAsync<BackupFileEntry>(sql, parameters, cancellationToken);

            var regex = new Regex(WildcardToRegex(namePattern), RegexOptions.IgnoreCase);
            entries = entryList.Where(f => regex.IsMatch(f.Path)).Take(limit).ToList();
        }
        else
        {
            sql += $" ORDER BY Path ASC LIMIT 1000"; // Fetch more for client side filtering
            var entryList = await _repository.QueryAsync<BackupFileEntry>(sql, parameters, cancellationToken);
            entries = entryList.Where(f => f.Path.Contains(namePattern, StringComparison.OrdinalIgnoreCase)).Take(limit).ToList();
        }

        if (entries.Count == 0) return new List<FileMetadataResult>();

        var backupSourceIds = entries.Select(e => e.BackupSourceId).Distinct().ToList();
        var backupSourcesTask = backupSourceIds.Select(id => _repository.GetAsync<BackupSource>(id, cancellationToken));
        var backupSourcesList = await Task.WhenAll(backupSourcesTask);
        var backupSources = backupSourcesList.Where(bs => bs != null).ToDictionary(bs => bs!.Id);

        return entries.Select(e => MapToResult(e, backupSources.GetValueOrDefault(e.BackupSourceId))).ToList();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<FileMetadataResult>> ListDirectoryAsync(string directoryPath, Guid? backupSourceId = null, bool includeSubdirectories = false, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Listing directory '{DirectoryPath}' (recursive: {Recursive})", directoryPath, includeSubdirectories);

        // Normalize directory path
        if (!directoryPath.EndsWith('/'))
        {
            directoryPath += "/";
        }

        var sql = "SELECT * FROM backupfileentry WHERE VersionDeleted = null";
        var parameters = new Dictionary<string, object>();

        if (backupSourceId.HasValue)
        {
            sql += " AND BackupSourceId = $backupSourceId";
            parameters.Add("backupSourceId", backupSourceId.Value);
        }

        // Fetch larger chunk to filter locally since prefix filtering requires specific SurrealQL logic
        sql += " ORDER BY Path ASC LIMIT 5000";
        var entryList = await _repository.QueryAsync<BackupFileEntry>(sql, parameters, cancellationToken);
        
        IEnumerable<BackupFileEntry> filtered;
        if (includeSubdirectories)
        {
            filtered = entryList.Where(f => f.Path.StartsWith(directoryPath));
        }
        else
        {
            filtered = entryList.Where(f => f.Path.StartsWith(directoryPath) && !f.Path.Substring(directoryPath.Length).Contains('/'));
        }
        var entries = filtered.ToList();

        if (entries.Count == 0) return new List<FileMetadataResult>();

        var backupSourceIds = entries.Select(e => e.BackupSourceId).Distinct().ToList();
        var backupSourcesTask = backupSourceIds.Select(id => _repository.GetAsync<BackupSource>(id, cancellationToken));
        var backupSourcesList = await Task.WhenAll(backupSourcesTask);
        var backupSources = backupSourcesList.Where(bs => bs != null).ToDictionary(bs => bs!.Id);

        return entries.Select(e => MapToResult(e, backupSources.GetValueOrDefault(e.BackupSourceId))).ToList();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<FileMetadataResult>> GetFilesModifiedBetweenAsync(DateTimeOffset startDate, DateTimeOffset endDate, Guid? backupSourceId = null, int limit = 100, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Getting files modified between {StartDate} and {EndDate}", startDate, endDate);

        var sql = "SELECT * FROM backupfileentry WHERE VersionDeleted = null AND LastModified >= $startDate AND LastModified <= $endDate";
        var parameters = new Dictionary<string, object>
        {
            { "startDate", startDate },
            { "endDate", endDate }
        };

        if (backupSourceId.HasValue)
        {
            sql += " AND BackupSourceId = $backupSourceId";
            parameters.Add("backupSourceId", backupSourceId.Value);
        }

        sql += $" ORDER BY LastModified DESC LIMIT {limit}";
        var entries = await _repository.QueryAsync<BackupFileEntry>(sql, parameters, cancellationToken);
        
        if (entries.Count == 0) return new List<FileMetadataResult>();

        var backupSourceIds = entries.Select(e => e.BackupSourceId).Distinct().ToList();
        var backupSourcesTask = backupSourceIds.Select(id => _repository.GetAsync<BackupSource>(id, cancellationToken));
        var backupSourcesList = await Task.WhenAll(backupSourcesTask);
        var backupSources = backupSourcesList.Where(bs => bs != null).ToDictionary(bs => bs!.Id);

        return entries.Select(e => MapToResult(e, backupSources.GetValueOrDefault(e.BackupSourceId))).ToList();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<FileVersionInfo>> GetCompleteVersionHistoryAsync(string path, Guid? backupSourceId = null, int limit = 50, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Getting complete version history for file '{Path}' from all backup versions", path);

        var sql = "SELECT * FROM backupversionfile WHERE Path = $path";
        var parameters = new Dictionary<string, object> { { "path", path } };

        if (backupSourceId.HasValue)
        {
            sql += " AND BackupSourceId = $backupSourceId";
            parameters.Add("backupSourceId", backupSourceId.Value);
        }

        sql += $" ORDER BY Version DESC LIMIT {limit}";

        var entries = await _repository.QueryAsync<BackupVersionFile>(sql, parameters, cancellationToken);

        return entries.Select(e => new FileVersionInfo
        {
            Id = e.Id,
            BackupSourceId = e.BackupSourceId,
            Path = e.Path,
            Hash = e.Hash,
            Size = e.Size,
            LastModified = e.LastModified,
            Version = e.Version,
            RecordedAt = e.RecordedAt
        }).ToList();
    }

    /// <inheritdoc />
    public async Task<FileVersionInfo?> GetLastModifiedAsync(string path, Guid? backupSourceId = null, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Getting last modified info for file '{Path}'", path);

        var sql = "SELECT * FROM backupversionfile WHERE Path = $path";
        var parameters = new Dictionary<string, object> { { "path", path } };

        if (backupSourceId.HasValue)
        {
            sql += " AND BackupSourceId = $backupSourceId";
            parameters.Add("backupSourceId", backupSourceId.Value);
        }

        sql += " ORDER BY LastModified DESC LIMIT 1";

        var entry = await _repository.QueryScalarAsync<BackupVersionFile>(sql, parameters, cancellationToken);

        if (entry == null)
        {
            _logger.LogWarning("No versions found for file '{Path}'", path);
            return null;
        }

        return new FileVersionInfo
        {
            Id = entry.Id,
            BackupSourceId = entry.BackupSourceId,
            Path = entry.Path,
            Hash = entry.Hash,
            Size = entry.Size,
            LastModified = entry.LastModified,
            Version = entry.Version,
            RecordedAt = entry.RecordedAt
        };
    }

    private static FileMetadataResult MapToResult(BackupFileEntry entry, BackupSource? backupSource)
    {
        return new FileMetadataResult
        {
            Id = entry.Id,
            BackupSourceId = entry.BackupSourceId,
            BackupSourceName = backupSource?.Name ?? "Unknown",
            Path = entry.Path,
            Hash = entry.Hash,
            Size = entry.Size,
            LastModified = entry.LastModified,
            VersionAdded = entry.VersionAdded,
            VersionDeleted = entry.VersionDeleted,
            IndexingStatus = entry.IndexingStatus
        };
    }

    /// <summary>
    /// Converts a wildcard pattern to a regex pattern.
    /// * matches any sequence of characters
    /// ? matches any single character
    /// </summary>
    private static string WildcardToRegex(string pattern)
    {
        return "^" + Regex.Escape(pattern)
            .Replace("\\*", ".*")
            .Replace("\\?", ".") + "$";
    }
}
