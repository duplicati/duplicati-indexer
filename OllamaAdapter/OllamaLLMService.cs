using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using DuplicatiIndexer.AdapterInterfaces;
using Microsoft.Extensions.Logging;

namespace DuplicatiIndexer.OllamaAdapter;

/// <summary>
/// Implementation of ILLMClient using Ollama's local chat API.
/// Supports models like Qwen3, Llama3.2, Gemma3, and Phi-4-mini.
/// </summary>
public class OllamaLLMService : ILLMClient
{
    private readonly HttpClient _httpClient;
    private readonly OllamaLLMConfig _config;
    private readonly ILogger<OllamaLLMService> _logger;
    private readonly string _model;
    private readonly string _baseUrl;

    /// <summary>
    /// Initializes a new instance of the <see cref="OllamaLLMService"/> class.
    /// </summary>
    /// <param name="httpClient">The HTTP client.</param>
    /// <param name="config">The Ollama LLM configuration.</param>
    /// <param name="logger">The logger.</param>
    public OllamaLLMService(HttpClient httpClient, OllamaLLMConfig config, ILogger<OllamaLLMService> logger)
    {
        _httpClient = httpClient;
        _config = config;
        _logger = logger;
        _model = _config.Model;
        _baseUrl = _config.BaseUrl;
    }

    /// <inheritdoc />
    public async Task<string> CompleteAsync(IEnumerable<ChatMessage> messages, CancellationToken cancellationToken = default)
    {
        if (messages == null)
        {
            throw new ArgumentNullException(nameof(messages));
        }

        var messageList = messages.ToList();
        _logger.LogDebug("Sending chat request to Ollama model {Model} with {MessageCount} messages", _model, messageList.Count);

        var url = $"{_baseUrl}/api/chat";

        // Convert messages to Ollama format
        var ollamaMessages = messageList.Select(m => new
        {
            role = ToRoleString(m.Role),
            content = m.Content
        }).ToArray();

        var requestBody = new
        {
            model = _model,
            messages = ollamaMessages,
            stream = false
        };

        var response = await _httpClient.PostAsJsonAsync(url, requestBody, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogError("Ollama chat request failed with status {StatusCode}: {ErrorContent}",
                response.StatusCode, errorContent);
            response.EnsureSuccessStatusCode();
        }

        var responseContent = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);

        if (!responseContent.TryGetProperty("message", out var messageProperty))
        {
            throw new InvalidOperationException("Ollama response does not contain 'message' property.");
        }

        if (!messageProperty.TryGetProperty("content", out var contentProperty))
        {
            throw new InvalidOperationException("Ollama response message does not contain 'content' property.");
        }

        var content = contentProperty.GetString();

        _logger.LogDebug("Successfully received response from Ollama model {Model}", _model);

        return content ?? string.Empty;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<string> StreamCompleteAsync(IEnumerable<ChatMessage> messages, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Fallback: Ollama implementation doesn't support true streaming yet. Just yield the final result.
        var content = await CompleteAsync(messages, cancellationToken);
        yield return content;
    }

    private static string ToRoleString(ChatRole role) => role switch
    {
        ChatRole.System => "system",
        ChatRole.User => "user",
        ChatRole.Assistant => "assistant",
        _ => throw new ArgumentOutOfRangeException(nameof(role), $"Unknown role: {role}")
    };
}
