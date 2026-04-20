using System.Net.Http.Headers;
using System.Text.Json;
using DuplicatiIndexer.AdapterInterfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace DuplicatiIndexer.UnstructuredAdapter;

/// <summary>
/// Implementation of IContentIndexer using the Unstructured API for text extraction.
/// </summary>
public class UnstructuredIndexer : IContentIndexer
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<UnstructuredIndexer> _logger;
    private readonly long _maxFileSizeBytes;

    private static readonly HashSet<string> _plainTextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".markdown", ".csv", ".json", ".xml", ".cs", ".js", ".ts", ".jsx", ".tsx",
        ".py", ".rb", ".php", ".java", ".go", ".rs", ".cpp", ".c", ".h", ".hpp", ".sql", ".sh",
        ".bash", ".ps1", ".bat", ".cmd", ".yaml", ".yml", ".toml", ".ini", ".conf", ".config",
        ".css", ".scss", ".sass", ".less", ".log"
    };

    /// <summary>
    /// Initializes a new instance of the <see cref="UnstructuredIndexer"/> class.
    /// </summary>
    /// <param name="httpClient">The HTTP client.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="configuration">The configuration.</param>
    public UnstructuredIndexer(HttpClient httpClient, ILogger<UnstructuredIndexer> logger, IConfiguration configuration)
    {
        _httpClient = httpClient;
        _logger = logger;
        _maxFileSizeBytes = configuration.GetValue<long>("Indexing:MaxFileSizeBytes", 104857600); // Default 100MB
    }

    /// <inheritdoc />
    public async Task<string> ExtractTextAsync(string filePath, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Extracting text from {FilePath} via Unstructured API", filePath);

        var fileInfo = new FileInfo(filePath);
        if (!fileInfo.Exists)
        {
            _logger.LogError("File not found: {FilePath}", filePath);
            throw new FileNotFoundException("File not found", filePath);
        }

        if (fileInfo.Length > _maxFileSizeBytes)
        {
            _logger.LogWarning("File {FilePath} exceeds maximum size limit of {MaxSize} bytes. Size: {ActualSize}",
                filePath, _maxFileSizeBytes, fileInfo.Length);
            throw new InvalidOperationException($"File size exceeds maximum limit of {_maxFileSizeBytes} bytes.");
        }

        // --- PLAINTEXT BYPASS (HUGE PERFORMANCE OPTIMIZATION) ---
        var extension = Path.GetExtension(filePath);
        if (_plainTextExtensions.Contains(extension) || string.IsNullOrEmpty(extension) || extension == ".")
        {
            _logger.LogInformation("Plaintext extension '{Extension}' detected. Bypassing heavy Python REST API locally natively...", extension);
            try
            {
                return await File.ReadAllTextAsync(filePath, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to read plaintext file {FilePath} organically natively, falling back to unstructured HTTP API...", filePath);
            }
        }
        // --------------------------------------------------------

        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 81920, useAsync: true);
        using var content = new MultipartFormDataContent();
        var fileContent = new StreamContent(stream);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Add(fileContent, "files", Path.GetFileName(filePath));

        var response = await _httpClient.PostAsync("/general/v0/general", content, cancellationToken);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        var elements = JsonSerializer.Deserialize<JsonElement[]>(json);

        if (elements is null || elements.Length == 0)
        {
            _logger.LogWarning("No elements returned from Unstructured API for {FilePath}", filePath);
            return string.Empty;
        }

        var textParts = new List<string>();
        foreach (var element in elements)
        {
            if (element.TryGetProperty("text", out var textProp))
            {
                var text = textProp.GetString();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    textParts.Add(text);
                }
            }
        }

        var result = string.Join("\n", textParts);
        _logger.LogInformation("Extracted {CharCount} characters from {FilePath}", result.Length, filePath);

        return result;
    }
}
