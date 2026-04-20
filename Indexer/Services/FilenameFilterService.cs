using DuplicatiIndexer.Configuration;
using DuplicatiIndexer.Data.Entities;
using DuplicatiIndexer.Data;

namespace DuplicatiIndexer.Services;

/// <summary>
/// Service for filtering files based on their extensions to determine which files need indexing.
/// </summary>
public class FilenameFilterService
{
    private readonly ISurrealRepository _repository;
    private readonly EnvironmentConfig _config;
    private readonly ILogger<FilenameFilterService> _logger;

    // Default file extensions that can be indexed for content
    private static readonly HashSet<string> DefaultIndexableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        // Documents
        ".txt", ".md", ".markdown",
        ".pdf",
        ".doc", ".docx",
        ".rtf",
        ".odt", ".ods", ".odp",

        // Spreadsheets
        ".xls", ".xlsx", ".csv",

        // Presentations
        ".ppt", ".pptx",

        // Web/Markup
        ".html", ".htm", ".xml", ".json",

        // Code files
        ".cs", ".js", ".ts", ".jsx", ".tsx",
        ".py", ".rb", ".php", ".java", ".go", ".rs",
        ".cpp", ".c", ".h", ".hpp",
        ".sql",
        ".sh", ".bash", ".ps1", ".bat", ".cmd",
        ".yaml", ".yml", ".toml", ".ini", ".conf", ".config",
        ".css", ".scss", ".sass", ".less",

        // Logs
        ".log",

        // E-books
        ".epub",
    };

    /// <summary>
    /// Initializes a new instance of the <see cref="FilenameFilterService"/> class.
    /// </summary>
    /// <param name="repository">The Surreal repository.</param>
    /// <param name="config">The environment configuration.</param>
    /// <param name="logger">The logger.</param>
    public FilenameFilterService(
        ISurrealRepository repository,
        EnvironmentConfig config,
        ILogger<FilenameFilterService> logger)
    {
        _repository = repository;
        _config = config;
        _logger = logger;
    }

    /// <summary>
    /// Gets the set of indexable file extensions based on configuration.
    /// If FileExtensionFilter starts with "!", only the specified extensions are used (exclusive mode).
    /// Otherwise, the specified extensions are added to the default list.
    /// </summary>
    /// <returns>A hash set of indexable file extensions.</returns>
    public HashSet<string> GetIndexableExtensions()
    {
        var filter = _config.Indexing.FileExtensionFilter?.Trim() ?? string.Empty;

        if (string.IsNullOrEmpty(filter))
        {
            // No custom filter specified, use defaults only
            return new HashSet<string>(DefaultIndexableExtensions, StringComparer.OrdinalIgnoreCase);
        }

        // Check for exclusive mode (starts with "!")
        if (filter.StartsWith("!"))
        {
            // Remove the "!" prefix and parse extensions
            var customExtensions = ParseExtensions(filter.Substring(1));
            _logger.LogInformation(
                "Using exclusive mode: Only the following {Count} custom extensions will be indexed: {Extensions}",
                customExtensions.Count, string.Join(", ", customExtensions));
            return customExtensions;
        }
        else
        {
            // Add mode: combine defaults with custom extensions
            var customExtensions = ParseExtensions(filter);
            var combinedExtensions = new HashSet<string>(DefaultIndexableExtensions, StringComparer.OrdinalIgnoreCase);

            foreach (var ext in customExtensions)
            {
                combinedExtensions.Add(ext);
            }

            _logger.LogInformation(
                "Using add mode: Added {CustomCount} custom extensions to {DefaultCount} default extensions. Total: {TotalCount}",
                customExtensions.Count, DefaultIndexableExtensions.Count, combinedExtensions.Count);

            return combinedExtensions;
        }
    }

    /// <summary>
    /// Parses a comma-separated list of file extensions.
    /// </summary>
    /// <param name="extensions">Comma-separated extensions (e.g., ".txt,.pdf,.docx").</param>
    /// <returns>A hash set of normalized extensions.</returns>
    private static HashSet<string> ParseExtensions(string extensions)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(extensions))
            return result;

        foreach (var ext in extensions.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            // Ensure extension starts with "."
            result.Add("." + ext.TrimStart('.'));
        }

        // Fix empty extension case
        if (result.Contains("."))
            result.Add("");

        return result;
    }

    /// <summary>
    /// Checks if a file has an indexable extension based on the current configuration.
    /// </summary>
    /// <param name="filePath">The file path to check.</param>
    /// <returns>True if the file has an indexable extension, otherwise false.</returns>
    public bool IsIndexableFile(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return false;

        var extension = Path.GetExtension(filePath);
        if (string.IsNullOrEmpty(extension))
            return false;

        var indexableExtensions = GetIndexableExtensions();
        return indexableExtensions.Contains(extension);
    }

    /// <summary>
    /// Filters files added in a specific version to find those that need indexing.
    /// </summary>
    /// <param name="backupSourceId">The backup source identifier.</param>
    /// <param name="version">The version timestamp.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A list of file entries that need indexing.</returns>
    public async Task<List<string>> GetFilesNeedingIndexingAsync(
        Guid backupSourceId,
        DateTimeOffset version,
        int? maxFileCount = null,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "Filtering files for indexing: BackupSourceId={BackupSourceId}, Version={Version}",
            backupSourceId, version);

        var files = new List<string>();
        int start = 0;
        const int batchSize = 10000;
        
        _logger.LogInformation("Paginating dataset via WebSocket chunks of {BatchSize}...", batchSize);

        while (true)
        {
            var batch = (await _repository.QueryAsync<string>(
                $"SELECT value Path FROM backupfileentry WHERE BackupSourceId = $backupSourceId AND IndexingStatus IN [0, 1] AND VersionDeleted = null LIMIT {batchSize} START {start}",
                new Dictionary<string, object>
                {
                    { "backupSourceId", backupSourceId }
                }, cancellationToken)).ToList();

            if (batch.Count == 0) break;
            
            files.AddRange(batch);
            start += batchSize;
        }

        _logger.LogInformation(
            "Found {TotalFiles} unindexed files for BackupSourceId={BackupSourceId}, Version={Version}",
            files.Count, backupSourceId, version);

        // Get the current set of indexable extensions
        var indexableExtensions = GetIndexableExtensions();
        _logger.LogInformation(
            "Using {ExtensionCount} indexable extensions: {Extensions}",
            indexableExtensions.Count, string.Join(", ", indexableExtensions));

        var indexableFiles = files
            .Where(f =>
            {
                var extension = Path.GetExtension(f);
                if (extension == null)
                    return false;
                return indexableExtensions.Contains(extension);
            })
            .ToList();

        if (maxFileCount.HasValue)
        {
            indexableFiles = indexableFiles.Take(maxFileCount.Value).ToList();
        }

        _logger.LogInformation(
            "Filtered to {IndexableFiles} indexable files (MaxLimit={MaxLimit}) for BackupSourceId={BackupSourceId}, Version={Version}",
            indexableFiles.Count, maxFileCount, backupSourceId, version);

        // Log some examples of skipped files for debugging
        var skippedCount = files.Count - indexableFiles.Count;
        if (skippedCount > 0)
        {
            var skippedExamples = files
                .Where(f => !indexableExtensions.Contains(Path.GetExtension(f) ?? string.Empty))
                .Take(5)
                .Select(f => Path.GetExtension(f) ?? "(no extension)")
                .Distinct()
                .ToList();

            _logger.LogDebug(
                "Skipped {SkippedCount} non-indexable files with extensions: {Extensions}",
                skippedCount, string.Join(", ", skippedExamples));
        }

        return indexableFiles;
    }

    /// <summary>
    /// Gets the default indexable extensions.
    /// </summary>
    public static IReadOnlySet<string> GetDefaultExtensions() => DefaultIndexableExtensions;
}
