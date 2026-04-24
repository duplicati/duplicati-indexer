using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using DuplicatiIndexer.AdapterInterfaces;
using Microsoft.Extensions.Logging;

namespace DuplicatiIndexer.ChatGPTAdapter;

/// <summary>
/// Implementation of ILLMClient using OpenAI's ChatGPT API.
/// </summary>
public class ChatGPTService : ILLMClient
{
    private readonly HttpClient _httpClient;
    private readonly ChatGPTConfig _config;
    private readonly ILogger<ChatGPTService> _logger;
    private readonly string _apiKey;

    /// <summary>
    /// Initializes a new instance of the <see cref="ChatGPTService"/> class.
    /// </summary>
    /// <param name="httpClient">The HTTP client.</param>
    /// <param name="config">The ChatGPT configuration.</param>
    /// <param name="logger">The logger.</param>
    /// <exception cref="InvalidOperationException">Thrown when ChatGPT API key is not configured.</exception>
    public ChatGPTService(HttpClient httpClient, ChatGPTConfig config, ILogger<ChatGPTService> logger)
    {
        _httpClient = httpClient;
        _config = config;
        _logger = logger;
        _apiKey = !string.IsNullOrEmpty(_config.ApiKey)
            ? _config.ApiKey
            : "dummy-key"; // Default to a dummy key if none is provided (useful for local OpenAI compatible servers)

        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
    }

    /// <inheritdoc />
    public async Task<string> CompleteAsync(IEnumerable<ChatMessage> messages, CancellationToken cancellationToken = default)
    {
        if (messages == null)
        {
            throw new ArgumentNullException(nameof(messages));
        }

        var url = $"{_config.BaseUrl.TrimEnd('/')}/chat/completions";

        var model = _config.Model;

        var messageList = messages.Select(m => new { role = ToRoleString(m.Role), content = m.Content }).ToArray();

        object requestBody;
        if (_apiKey == "lm-studio")
        {
            bool isModelLoaded = false;
            try
            {
                var v0ModelsUrl = url.Replace("/v1/chat/completions", "/api/v0/models");
                var modelsResponse = await _httpClient.GetAsync(v0ModelsUrl, cancellationToken);
                if (modelsResponse.IsSuccessStatusCode)
                {
                    var modelsJson = await modelsResponse.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
                    if (modelsJson.TryGetProperty("data", out var dataArray))
                    {
                        foreach (var m in dataArray.EnumerateArray())
                        {
                            if (m.TryGetProperty("state", out var stateProp) && stateProp.GetString() == "loaded")
                            {
                                if (m.TryGetProperty("type", out var typeProp))
                                {
                                    var typeStr = typeProp.GetString();
                                    if (typeStr == "llm" || typeStr == "vlm")
                                    {
                                        isModelLoaded = true;
                                        break;
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to inspect LM Studio loaded state via /api/v0/models");
            }

            if (isModelLoaded)
            {
                // Already loaded, act as proxy without triggering JIT
                requestBody = new
                {
                    model = model,
                    messages = messageList
                };
            }
            else
            {
                // Not loaded, force JIT load with requested context bounds
                requestBody = new
                {
                    model = model,
                    messages = messageList,
                    n_ctx = 8192,
                    n_batch = 8192
                };
            }
        }
        else
        {
            requestBody = new
            {
                model = model,
                messages = messageList
            };
        }

        HttpResponseMessage response = null;
        int maxRetries = 2;
        for (int i = 0; i < maxRetries; i++)
        {
            response = await _httpClient.PostAsJsonAsync(url, requestBody, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
                
                // LM Studio throws a 400 Bad Request when it JIT unloads/reloads a model due to setting changes
                if (_apiKey == "lm-studio" && errorBody.Contains("Model reloaded", StringComparison.OrdinalIgnoreCase) && i < maxRetries - 1)
                {
                    _logger.LogWarning("LM Studio reloaded the model. Retrying the completion request...");
                    continue;
                }
                
                throw new HttpRequestException($"Response status code does not indicate success: {(int)response.StatusCode} ({response.ReasonPhrase}). Error: {errorBody}");
            }
            break; // Success
        }

        var responseContent = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        var content = responseContent.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();

        return content ?? string.Empty;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<string> StreamCompleteAsync(IEnumerable<ChatMessage> messages, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (messages == null) throw new ArgumentNullException(nameof(messages));

        var url = $"{_config.BaseUrl.TrimEnd('/')}/chat/completions";
        var model = _config.Model;
        var messageList = messages.Select(m => new { role = ToRoleString(m.Role), content = m.Content }).ToArray();

        var requestBody = new
        {
            model = model,
            messages = messageList,
            stream = true
        };

        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(requestBody)
        };

        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException($"Response status code does not indicate success: {(int)response.StatusCode} ({response.ReasonPhrase}). Error: {errorBody}");
        }

        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);

        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync();
            if (line == null) break;
            
            if (string.IsNullOrWhiteSpace(line)) continue;

            if (line.StartsWith("data: "))
            {
                var data = line.Substring(6).Trim();
                if (data == "[DONE]") break;

                string contentToYield = null;
                try
                {
                    using var doc = JsonDocument.Parse(data);
                    var choices = doc.RootElement.GetProperty("choices");
                    if (choices.GetArrayLength() > 0)
                    {
                        var delta = choices[0].GetProperty("delta");
                        if (delta.TryGetProperty("content", out var contentProp))
                        {
                            contentToYield = contentProp.GetString();
                        }
                    }
                }
                catch (JsonException)
                {
                    // Ignore malformed JSON chunks
                }

                if (!string.IsNullOrEmpty(contentToYield))
                {
                    yield return contentToYield;
                }
            }
        }
    }

    private static string ToRoleString(ChatRole role) => role switch
    {
        ChatRole.System => "system",
        ChatRole.User => "user",
        ChatRole.Assistant => "assistant",
        _ => throw new ArgumentOutOfRangeException(nameof(role), $"Unknown role: {role}")
    };
}
