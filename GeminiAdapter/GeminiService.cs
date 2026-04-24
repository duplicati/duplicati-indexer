using DuplicatiIndexer.AdapterInterfaces;
using Microsoft.Extensions.Logging;
using System.Runtime.CompilerServices;
using System.Net.Http.Json;
using System.Text.Json;

namespace DuplicatiIndexer.GeminiAdapter;

/// <summary>
/// Implementation of ILLMClient using Google's Gemini API.
/// </summary>
public class GeminiService : ILLMClient
{
    private readonly HttpClient _httpClient;
    private readonly GeminiConfig _config;
    private readonly ILogger<GeminiService> _logger;
    private readonly string _apiKey;

    /// <summary>
    /// Initializes a new instance of the <see cref="GeminiService"/> class.
    /// </summary>
    /// <param name="httpClient">The HTTP client.</param>
    /// <param name="config">The Gemini configuration.</param>
    /// <param name="logger">The logger.</param>
    /// <exception cref="InvalidOperationException">Thrown when Gemini API key is not configured.</exception>
    public GeminiService(HttpClient httpClient, GeminiConfig config, ILogger<GeminiService> logger)
    {
        _httpClient = httpClient;
        _config = config;
        _logger = logger;
        _apiKey = !string.IsNullOrEmpty(_config.ApiKey)
            ? _config.ApiKey
            : throw new InvalidOperationException("Gemini API key is missing.");
    }

    /// <inheritdoc />
    public async Task<string> CompleteAsync(IEnumerable<ChatMessage> messages, CancellationToken cancellationToken = default)
    {
        if (messages == null)
        {
            throw new ArgumentNullException(nameof(messages));
        }

        var model = _config.Model;
        var url = $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent?key={_apiKey}";

        // Gemini uses a different format - combine all messages into parts
        // System messages are prepended to the first user message with a separator
        var messageList = messages.ToList();
        var systemContent = string.Join("\n\n", messageList.Where(m => m.Role == ChatRole.System).Select(m => m.Content));
        var nonSystemMessages = messageList.Where(m => m.Role != ChatRole.System).ToList();

        var parts = new List<object>();

        if (!string.IsNullOrEmpty(systemContent) && nonSystemMessages.Count > 0)
        {
            // Prepend system message to first user message
            var firstMessage = nonSystemMessages.First();
            var combinedContent = $"{systemContent}\n\n---\n\n{firstMessage.Content}";
            parts.Add(new { text = combinedContent });

            // Add remaining messages
            foreach (var msg in nonSystemMessages.Skip(1))
            {
                parts.Add(new { text = msg.Content });
            }
        }
        else
        {
            // Just add all non-system messages
            foreach (var msg in nonSystemMessages)
            {
                parts.Add(new { text = msg.Content });
            }
        }

        var requestBody = new
        {
            contents = new[]
            {
                new
                {
                    parts = parts.ToArray()
                }
            }
        };

        var response = await _httpClient.PostAsJsonAsync(url, requestBody, cancellationToken);
        response.EnsureSuccessStatusCode();

        var responseContent = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        
        // Check for prompt feedback (indicates blocked content)
        if (responseContent.TryGetProperty("promptFeedback", out var promptFeedback))
        {
            if (promptFeedback.TryGetProperty("blockReason", out var blockReason))
            {
                var reason = blockReason.GetString() ?? "unknown";
                _logger.LogWarning("Gemini request blocked. Reason: {BlockReason}", reason);
                throw new InvalidOperationException($"Content blocked by Gemini. Reason: {reason}");
            }
        }

        // Check for candidates array
        if (!responseContent.TryGetProperty("candidates", out var candidates) || candidates.GetArrayLength() == 0)
        {
            _logger.LogError("Gemini response missing candidates array or empty. Response: {Response}", responseContent.ToString());
            throw new InvalidOperationException("Gemini response did not contain any candidates.");
        }

        var firstCandidate = candidates[0];

        // Check finish reason first - this explains why content might be missing
        var finishReasonValue = "UNKNOWN";
        if (firstCandidate.TryGetProperty("finishReason", out var finishReason))
        {
            finishReasonValue = finishReason.GetString() ?? "UNKNOWN";
            if (finishReasonValue != "STOP" && finishReasonValue != "MAX_TOKENS")
            {
                _logger.LogWarning("Gemini finish reason indicates potential issue: {FinishReason}", finishReasonValue);
            }
        }

        // Extract safety ratings if available (provides details on why content was blocked)
        var safetyInfo = "";
        if (firstCandidate.TryGetProperty("safetyRatings", out var safetyRatings))
        {
            var ratings = new List<string>();
            foreach (var rating in safetyRatings.EnumerateArray())
            {
                if (rating.TryGetProperty("category", out var category) && rating.TryGetProperty("probability", out var probability))
                {
                    var prob = probability.GetString();
                    if (prob != "NEGLIGIBLE" && prob != "LOW")
                    {
                        ratings.Add($"{category.GetString()}: {prob}");
                    }
                }
            }
            if (ratings.Count > 0)
            {
                safetyInfo = string.Join(", ", ratings);
            }
        }

        // Safely extract content
        if (!firstCandidate.TryGetProperty("content", out var contentElement))
        {
            _logger.LogError("Gemini candidate missing content property. Candidate: {Candidate}", firstCandidate.ToString());
            throw new InvalidOperationException("Gemini response candidate missing content.");
        }

        // Extract usage metadata for diagnostics
        var promptTokens = 0;
        if (responseContent.TryGetProperty("usageMetadata", out var usageMetadata) &&
            usageMetadata.TryGetProperty("promptTokenCount", out var promptTokenCount))
        {
            promptTokens = promptTokenCount.GetInt32();
        }

        // Check if parts exists - if not, this might be due to finish reason
        if (!contentElement.TryGetProperty("parts", out JsonElement partsElement) || partsElement.GetArrayLength() == 0)
        {
            _logger.LogInformation("Full Gemini response: {Response}", responseContent.ToString());
            _logger.LogError("Gemini content missing parts array or empty. FinishReason: {FinishReason}, PromptTokens: {PromptTokens}, SafetyRatings: {SafetyRatings}, Content: {Content}",
                finishReasonValue, promptTokens, safetyInfo, contentElement.ToString());
            
            // If finish reason indicates a block, throw specific error with safety details
            if (finishReasonValue is "SAFETY" or "RECITATION" or "OTHER")
            {
                var details = string.IsNullOrEmpty(safetyInfo)
                    ? $"Finish reason: {finishReasonValue}"
                    : $"Finish reason: {finishReasonValue}. Safety ratings: {safetyInfo}";
                throw new InvalidOperationException($"Gemini response blocked or empty. {details}");
            }
            
            // For STOP with no parts, throw error (model chose not to generate)
            if (finishReasonValue == "STOP")
            {
                var tokenInfo = promptTokens > 0 ? $" Prompt tokens: {promptTokens}." : "";
                var safetyDetails = string.IsNullOrEmpty(safetyInfo) ? "" : $" Safety ratings: {safetyInfo}.";
                throw new InvalidOperationException($"Gemini model returned STOP with no generated content.{tokenInfo}{safetyDetails} This may be due to the prompt exceeding the model's context window or the model being unable to generate a response.");
            }
            
            throw new InvalidOperationException($"Gemini response content missing parts. Finish reason: {finishReasonValue}");
        }

        var firstPart = partsElement[0];
        if (!firstPart.TryGetProperty("text", out var textElement))
        {
            _logger.LogError("Gemini part missing text property. Part: {Part}", firstPart.ToString());
            throw new InvalidOperationException("Gemini response part missing text.");
        }

        var content = textElement.GetString();
        return content ?? string.Empty;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<string> StreamCompleteAsync(IEnumerable<ChatMessage> messages, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Fallback: Gemini implementation doesn't support true streaming yet. Just yield the final result.
        var content = await CompleteAsync(messages, cancellationToken);
        yield return content;
    }
}
