using DuplicatiIndexer.AdapterInterfaces;
using Microsoft.Extensions.Logging;
using System.Runtime.CompilerServices;
using System.Net.Http.Json;
using System.Text.Json;

namespace DuplicatiIndexer.ClaudeAdapter;

/// <summary>
/// Implementation of ILLMClient using Anthropic's Claude API.
/// </summary>
public class ClaudeService : ILLMClient
{
    private readonly HttpClient _httpClient;
    private readonly ClaudeConfig _config;
    private readonly ILogger<ClaudeService> _logger;
    private readonly string _apiKey;

    /// <summary>
    /// Initializes a new instance of the <see cref="ClaudeService"/> class.
    /// </summary>
    /// <param name="httpClient">The HTTP client.</param>
    /// <param name="config">The Claude configuration.</param>
    /// <param name="logger">The logger.</param>
    /// <exception cref="InvalidOperationException">Thrown when Claude API key is not configured.</exception>
    public ClaudeService(HttpClient httpClient, ClaudeConfig config, ILogger<ClaudeService> logger)
    {
        _httpClient = httpClient;
        _config = config;
        _logger = logger;
        _apiKey = !string.IsNullOrEmpty(_config.ApiKey)
            ? _config.ApiKey
            : throw new InvalidOperationException("Claude API key is missing.");

        _httpClient.DefaultRequestHeaders.Add("x-api-key", _apiKey);
        _httpClient.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");
    }

    /// <inheritdoc />
    public async Task<string> CompleteAsync(IEnumerable<ChatMessage> messages, CancellationToken cancellationToken = default)
    {
        if (messages == null)
        {
            throw new ArgumentNullException(nameof(messages));
        }

        var url = "https://api.anthropic.com/v1/messages";

        var model = _config.Model;
        var maxTokens = _config.MaxTokens;

        // Claude expects messages with roles, but system is handled separately via system parameter
        var systemMessage = messages.FirstOrDefault(m => m.Role == ChatRole.System)?.Content;
        var conversationMessages = messages.Where(m => m.Role != ChatRole.System)
            .Select(m => new { role = ToRoleString(m.Role), content = m.Content })
            .ToArray();

        var requestBody = new Dictionary<string, object>
        {
            ["model"] = model,
            ["max_tokens"] = maxTokens,
            ["messages"] = conversationMessages
        };

        if (!string.IsNullOrEmpty(systemMessage))
        {
            requestBody["system"] = systemMessage;
        }

        var response = await _httpClient.PostAsJsonAsync(url, requestBody, cancellationToken);
        response.EnsureSuccessStatusCode();

        var responseContent = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        var content = responseContent.GetProperty("content")[0].GetProperty("text").GetString();

        return content ?? string.Empty;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<string> StreamCompleteAsync(IEnumerable<ChatMessage> messages, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Fallback: Claude implementation doesn't support true streaming yet. Just yield the final result.
        var content = await CompleteAsync(messages, cancellationToken);
        yield return content;
    }

    private static string ToRoleString(ChatRole role) => role switch
    {
        ChatRole.User => "user",
        ChatRole.Assistant => "assistant",
        _ => throw new ArgumentOutOfRangeException(nameof(role), $"Role {role} is not supported for Claude conversation messages. Use System role only as the system parameter.")
    };
}
