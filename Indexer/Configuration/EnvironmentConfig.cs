using DuplicatiIndexer.ChatGPTAdapter;
using DuplicatiIndexer.ClaudeAdapter;
using DuplicatiIndexer.GeminiAdapter;
using DuplicatiIndexer.OllamaAdapter;

namespace DuplicatiIndexer.Configuration;

/// <summary>
/// Configuration class for environment variables and application settings.
/// Maps to environment variables with the "" prefix (e.g., CONNECTIONSTRINGS__DOCUMENTSTORE).
/// </summary>
public class EnvironmentConfig
{
    /// <summary>
    /// Gets or sets the connection strings configuration.
    /// </summary>
    public ConnectionStringsConfig ConnectionStrings { get; set; } = new();

    /// <summary>
    /// Gets or sets the LLM configuration.
    /// </summary>
    public LlmConfig Llm { get; set; } = new();

    /// <summary>
    /// Gets or sets the Embed configuration.
    /// </summary>
    public EmbedConfig Embed { get; set; } = new();

    /// <summary>
    /// Gets or sets the Gemini configuration.
    /// </summary>
    public GeminiConfig Gemini { get; set; } = new();

    /// <summary>
    /// Gets or sets the ChatGPT configuration.
    /// </summary>
    public ChatGPTConfig ChatGPT { get; set; } = new();

    /// <summary>
    /// Gets or sets the LMStudio configuration.
    /// </summary>
    public LMStudioConfig LMStudio { get; set; } = new();

    /// <summary>
    /// Gets or sets the Claude configuration.
    /// </summary>
    public ClaudeConfig Claude { get; set; } = new();

    /// <summary>
    /// Gets or sets the Ollama embedding configuration.
    /// </summary>
    public OllamaEmbeddingConfig OllamaEmbed { get; set; } = new();

    /// <summary>
    /// Gets or sets the Ollama LLM configuration for chat completions.
    /// </summary>
    public OllamaLLMConfig OllamaLLM { get; set; } = new();

    /// <summary>
    /// Gets or sets the Unstructured configuration.
    /// </summary>
    public UnstructuredConfig Unstructured { get; set; } = new();

    /// <summary>
    /// Gets or sets the MarkItDown configuration.
    /// </summary>
    public MarkItDownConfig MarkItDown { get; set; } = new();

    /// <summary>
    /// Gets or sets the vector store configuration.
    /// </summary>
    public VectorStoreConfig VectorStore { get; set; } = new();

    /// <summary>
    /// Gets or sets the indexing configuration.
    /// </summary>
    public IndexingConfig Indexing { get; set; } = new();

    /// <summary>
    /// Gets or sets the chunking configuration.
    /// </summary>
    public ChunkingConfig Chunking { get; set; } = new();

    /// <summary>
    /// Gets or sets the logging configuration.
    /// </summary>
    public LoggingConfig Logging { get; set; } = new();

    /// <summary>
    /// Gets or sets the RAG query service configuration.
    /// </summary>
    public RagQueryConfig RagQuery { get; set; } = new();

    /// <summary>
    /// Gets or sets the security configuration.
    /// </summary>
    public SecurityConfig Security { get; set; } = new();

    /// <summary>
    /// Connection strings configuration.
    /// </summary>
    public class ConnectionStringsConfig
    {
        /// <summary>
        /// PostgreSQL connection string for Marten document store.
        /// Environment variable: CONNECTIONSTRINGS__DOCUMENTSTORE
        /// </summary>
        public string DocumentStore { get; set; } = string.Empty;

        /// <summary>
        /// PostgreSQL connection string for Wolverine message store.
        /// Environment variable: CONNECTIONSTRINGS__MESSAGESTORE
        /// </summary>
        public string MessageStore { get; set; } = string.Empty;

    }

    /// <summary>
    /// LLM provider configuration.
    /// </summary>
    public class LlmConfig
    {
        /// <summary>
        /// LLM provider name (gemini, chatgpt, claude, ollama, lmstudio, qwen3, llama3.2, gemma3, phi-4-mini).
        /// Environment variable: LLM__PROVIDER
        /// </summary>
        public string Provider { get; set; } = "gemini";
    }

    /// <summary>
    /// Embedding provider configuration.
    /// </summary>
    public class EmbedConfig
    {
        /// <summary>
        /// Embedding provider name (ollama, lmstudio).
        /// Environment variable: EMBED__PROVIDER
        /// </summary>
        public string Provider { get; set; } = "ollama";
    }

    /// <summary>
    /// LMStudio configuration.
    /// </summary>
    public class LMStudioConfig
    {
        /// <summary>
        /// Base URL for the LMStudio local server.
        /// Environment variable: LMSTUDIO__BASEURL
        /// </summary>
        public string BaseUrl { get; set; } = "http://localhost:1234/v1";

        /// <summary>
        /// Model name to use for LMStudio.
        /// Environment variable: LMSTUDIO__MODEL
        /// </summary>
        public string Model { get; set; } = "local-model";

        /// <summary>
        /// Embed Model name to use for LMStudio.
        /// Environment variable: LMSTUDIO__EMBEDMODEL
        /// </summary>
        public string EmbedModel { get; set; } = "text-embedding-nomic-embed-text-v1.5";
    }



    /// <summary>
    /// Unstructured service configuration.
    /// </summary>
    public class UnstructuredConfig
    {
        /// <summary>
        /// Base URL for the Unstructured API.
        /// Environment variable: UNSTRUCTURED__BASEURL
        /// </summary>
        public string BaseUrl { get; set; } = "http://localhost:8000";
    }

    /// <summary>
    /// MarkItDown service configuration.
    /// </summary>
    public class MarkItDownConfig
    {
        /// <summary>
        /// Path to the MarkItDown executable.
        /// Environment variable: MARKITDOWN__EXECUTABLEPATH
        /// </summary>
        public string ExecutablePath { get; set; } = "markitdown";
    }

    /// <summary>
    /// Vector store configuration.
    /// </summary>
    public class VectorStoreConfig
    {
        /// <summary>
        /// Vector store provider (qdrant or chroma).
        /// Environment variable: VECTORSTORE__PROVIDER
        /// </summary>
        public string Provider { get; set; } = "qdrant";

        /// <summary>
        /// Collection name in the vector store.
        /// Environment variable: VECTORSTORE__COLLECTIONNAME
        /// </summary>
        public string CollectionName { get; set; } = "backup_content";
    }

    /// <summary>
    /// Indexing configuration.
    /// </summary>
    public class IndexingConfig
    {
        /// <summary>
        /// Maximum file size in bytes to index.
        /// Environment variable: INDEXING__MAXFILESIZEBYTES
        /// </summary>
        public long MaxFileSizeBytes { get; set; } = 104857600; // 100 MB

        /// <summary>
        /// File extension filter for indexing.
        /// If starts with "!", only the specified extensions are used (no defaults).
        /// Otherwise, the specified extensions are added to the default list.
        /// Format: ".txt,.pdf,.docx" or "!.txt,.pdf" (exclusive mode)
        /// Environment variable: INDEXING__FILEEXTENSIONFILTER
        /// </summary>
        public string FileExtensionFilter { get; set; } = string.Empty;

        /// <summary>
        /// Content indexer provider (unstructured or markitdown).
        /// Environment variable: INDEXING__PROVIDER
        /// </summary>
        public string Provider { get; set; } = "unstructured";

        /// <summary>
        /// Flag to enable incremental processing.
        /// Environment variable: INDEXING__ISINCREMENTALRUN
        /// </summary>
        public bool IsIncrementalRun { get; set; } = false;
    }

    /// <summary>
    /// Chunking configuration for text splitting before embedding.
    /// </summary>
    public class ChunkingConfig
    {
        /// <summary>
        /// Maximum chunk size in tokens. Text longer than this will be split into multiple chunks.
        /// Default is 512 tokens to deeply respect restrictive local hardware matrix sizes and maximize vector similarity accuracy.
        /// Environment variable: CHUNKING__MAXCHUNKSIZE
        /// </summary>
        public int MaxChunkSize { get; set; } = 512;

        /// <summary>
        /// Overlap size in tokens between consecutive chunks to maintain context.
        /// Environment variable: CHUNKING__OVERLAPSIZE
        /// </summary>
        public int OverlapSize { get; set; } = 50;

        /// <summary>
        /// Approximate characters per token for token counting.
        /// Default is 1.5, which strongly aligns with highly dense text structures like Enron metadata and HTML dumps.
        /// Environment variable: CHUNKING__CHARSPERTOKEN
        /// </summary>
        public double CharsPerToken { get; set; } = 1.5;
    }

    /// <summary>
    /// Logging configuration.
    /// </summary>
    public class LoggingConfig
    {
        /// <summary>
        /// Gets or sets the log level configuration.
        /// </summary>
        public LogLevelConfig LogLevel { get; set; } = new();
    }

    /// <summary>
    /// Log level configuration.
    /// </summary>
    public class LogLevelConfig
    {
        /// <summary>
        /// Default log level.
        /// Environment variable: LOGGING__LOGLEVEL__DEFAULT
        /// </summary>
        public string Default { get; set; } = "Information";
    }

    /// <summary>
    /// RAG query service configuration.
    /// </summary>
    public class RagQueryConfig
    {
        /// <summary>
        /// RAG query service version to use (v1 or v2).
        /// v1: Traditional RAG with automatic embedding and search
        /// v2: ReAct pattern where LLM controls the search process
        /// Environment variable: RAGQUERY__VERSION
        /// </summary>
        public string Version { get; set; } = "v2";
    }

    /// <summary>
    /// Security configuration for threat state monitoring.
    /// </summary>
    public class SecurityConfig
    {
        /// <summary>
        /// Threat state monitor provider (noop or threatstatemonitor).
        /// "noop" disables threat monitoring (default).
        /// "threatstatemonitor" enables the full ThreatStateMonitor with canary detection and velocity tracking.
        /// Environment variable: SECURITY__THREATMONITOR
        /// </summary>
        public string ThreatMonitor { get; set; } = "noop";
    }
}
