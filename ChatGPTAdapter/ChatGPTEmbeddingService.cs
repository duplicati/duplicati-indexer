using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using DuplicatiIndexer.AdapterInterfaces;
using Microsoft.Extensions.Logging;

namespace DuplicatiIndexer.ChatGPTAdapter;

/// <summary>
/// Implementation of IEmbeddingService using OpenAI's compatible API.
/// </summary>
public class ChatGPTEmbeddingService : IEmbeddingService
{
    private readonly HttpClient _httpClient;
    private readonly ChatGPTEmbeddingConfig _config;
    private readonly ILogger<ChatGPTEmbeddingService> _logger;
    private readonly string _apiKey;

    public ChatGPTEmbeddingService(HttpClient httpClient, ChatGPTEmbeddingConfig config, ILogger<ChatGPTEmbeddingService> logger)
    {
        _httpClient = httpClient;
        _config = config;
        _logger = logger;
        _apiKey = !string.IsNullOrEmpty(_config.ApiKey)
            ? _config.ApiKey
            : "dummy-key";

        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
    }

    /// <inheritdoc />
    public async Task<float[]> GenerateEmbeddingAsync(string text, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new ArgumentException("Text cannot be null or empty.", nameof(text));
        }

        var url = $"{_config.BaseUrl.TrimEnd('/')}/embeddings";

        // Distribute array batch load dynamically across potentially scaled LMStudio instances via model arrays
        var models = _config.EmbedModel.Split(',').Select(m => m.Trim()).ToArray();
        var selectedModel = models[Random.Shared.Next(models.Length)];

        if (_apiKey == "lm-studio")
        {
            try
            {
                var v0ModelsUrl = url.Replace("/v1/embeddings", "/api/v0/models");
                using var requestMessage = new HttpRequestMessage(HttpMethod.Get, v0ModelsUrl);
                var modelsResponse = await _httpClient.SendAsync(requestMessage, cancellationToken);
                if (modelsResponse.IsSuccessStatusCode)
                {
                    var modelsJson = await modelsResponse.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
                    if (modelsJson.TryGetProperty("data", out var dataArray))
                    {
                        foreach (var m in dataArray.EnumerateArray())
                        {
                            if (m.TryGetProperty("state", out var stateProp) && stateProp.GetString() == "loaded")
                            {
                                if (m.TryGetProperty("type", out var typeProp) && typeProp.GetString() == "embedding")
                                {
                                    if (m.TryGetProperty("id", out var idProp))
                                    {
                                        selectedModel = idProp.GetString();
                                    }
                                    break;
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to inspect LM Studio loaded state via /api/v0/models for embeddings");
            }
        }

        var requestBody = new
        {
            model = selectedModel,
            input = text
        };

        var response = await _httpClient.PostAsJsonAsync(url, requestBody, cancellationToken);
        
        if (!response.IsSuccessStatusCode)
        {
            var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogError("Embedding request failed with status {StatusCode}: {ErrorContent}", 
                response.StatusCode, errorContent);
            response.EnsureSuccessStatusCode();
        }

        var responseContent = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        
        if (!responseContent.TryGetProperty("data", out var dataProperty))
        {
            throw new InvalidOperationException("Embedding response does not contain 'data' property.");
        }

        var embeddingProperty = dataProperty[0].GetProperty("embedding");
        var embedding = embeddingProperty.EnumerateArray().Select(x => x.GetSingle()).ToArray();

        return embedding;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<float[]>> GenerateEmbeddingsAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken = default)
    {
        if (texts == null || texts.Count == 0)
        {
            return Array.Empty<float[]>();
        }

        // By mapping the entire texts array into the 'input' JSON field natively, we bypass 
        // individual HTTP request interleaving. Local pipelines like llama-server natively support 
        // Array evaluations in singular contiguous matrix multiplication passes across Apple Silicon Metal buffers.
        var url = $"{_config.BaseUrl.TrimEnd('/')}/embeddings";

        // Distribute array batch load dynamically across potentially scaled LMStudio instances via model arrays
        var models = _config.EmbedModel.Split(',').Select(m => m.Trim()).ToArray();
        var selectedModel = models[Random.Shared.Next(models.Length)];

        if (_apiKey == "lm-studio")
        {
            try
            {
                var v0ModelsUrl = url.Replace("/v1/embeddings", "/api/v0/models");
                using var requestMessage = new HttpRequestMessage(HttpMethod.Get, v0ModelsUrl);
                var modelsResponse = await _httpClient.SendAsync(requestMessage, cancellationToken);
                if (modelsResponse.IsSuccessStatusCode)
                {
                    var modelsJson = await modelsResponse.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
                    if (modelsJson.TryGetProperty("data", out var dataArray))
                    {
                        foreach (var m in dataArray.EnumerateArray())
                        {
                            if (m.TryGetProperty("state", out var stateProp) && stateProp.GetString() == "loaded")
                            {
                                if (m.TryGetProperty("type", out var typeProp) && typeProp.GetString() == "embedding")
                                {
                                    if (m.TryGetProperty("id", out var idProp))
                                    {
                                        selectedModel = idProp.GetString();
                                    }
                                    break;
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to inspect LM Studio loaded state via /api/v0/models for batch embeddings");
            }
        }

        var requestBody = new
        {
            model = selectedModel,
            input = texts
        };

        var response = await _httpClient.PostAsJsonAsync(url, requestBody, cancellationToken);
        
        if (!response.IsSuccessStatusCode)
        {
            var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogError("Batch embedding request failed with status {StatusCode}: {ErrorContent}", 
                response.StatusCode, errorContent);
            response.EnsureSuccessStatusCode();
        }

        var responseContent = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        
        if (!responseContent.TryGetProperty("data", out var dataProperty))
        {
            throw new InvalidOperationException("Embedding response does not contain 'data' property.");
        }

        // The response contains an array of embedding objects mapped by index
        var embeddings = new List<float[]>();
        foreach (var item in dataProperty.EnumerateArray().OrderBy(x => x.GetProperty("index").GetInt32()))
        {
            var embeddingProperty = item.GetProperty("embedding");
            var embedding = embeddingProperty.EnumerateArray().Select(x => x.GetSingle()).ToArray();
            embeddings.Add(embedding);
        }

        return embeddings;
    }
}
