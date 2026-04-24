namespace DuplicatiIndexer.AdapterInterfaces;

/// <summary>
/// Defines the role of a message sender in a conversation with an LLM.
/// </summary>
public enum ChatRole
{
    /// <summary>
    /// The system message sets the context and behavior for the assistant.
    /// </summary>
    System,

    /// <summary>
    /// The user message represents input from the end user.
    /// </summary>
    User,

    /// <summary>
    /// The assistant message represents a response from the LLM.
    /// </summary>
    Assistant
}

/// <summary>
/// A message in a conversation with an LLM.
/// </summary>
public record ChatMessage
{
    /// <summary>
    /// Gets the role of the message sender.
    /// </summary>
    public required ChatRole Role { get; init; }

    /// <summary>
    /// Gets the content of the message.
    /// </summary>
    public required string Content { get; init; }
}

/// <summary>
/// Low-level client for communicating with a Large Language Model.
/// Implementations handle the transport-specific details (HTTP, authentication, etc.)
/// while the interface provides a unified way to send prompts and receive completions.
/// </summary>
public interface ILLMClient
{
    /// <summary>
    /// Sends a conversation with multiple messages to the LLM and returns the completion.
    /// </summary>
    /// <param name="messages">The conversation messages.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The generated completion text.</returns>
    Task<string> CompleteAsync(IEnumerable<ChatMessage> messages, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends a conversation to the LLM and streams the response tokens as they are generated.
    /// </summary>
    /// <param name="messages">The conversation messages.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>An async enumerable of generated completion tokens.</returns>
    IAsyncEnumerable<string> StreamCompleteAsync(IEnumerable<ChatMessage> messages, CancellationToken cancellationToken = default);
}
