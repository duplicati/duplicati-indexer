FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy the solution and project files
COPY ["DuplicatiIndexer.slnx", "./"]
COPY ["Indexer/DuplicatiIndexer.csproj", "Indexer/"]
COPY ["Indexer.Tests/Indexer.Tests.csproj", "Indexer.Tests/"]
COPY ["AdapterInterfaces/AdapterInterfaces.csproj", "AdapterInterfaces/"]
COPY ["GeminiAdapter/GeminiAdapter.csproj", "GeminiAdapter/"]
COPY ["ClaudeAdapter/ClaudeAdapter.csproj", "ClaudeAdapter/"]
COPY ["ChatGPTAdapter/ChatGPTAdapter.csproj", "ChatGPTAdapter/"]
COPY ["OllamaAdapter/OllamaAdapter.csproj", "OllamaAdapter/"]
COPY ["UnstructuredAdapter/UnstructuredAdapter.csproj", "UnstructuredAdapter/"]
COPY ["MarkItDownAdapter/MarkItDownAdapter.csproj", "MarkItDownAdapter/"]
COPY ["SurrealAdapter/SurrealAdapter.csproj", "SurrealAdapter/"]

# Copy the duplicati submodule
COPY ["modules/duplicati/", "modules/duplicati/"]

# Restore dependencies
RUN dotnet restore "Indexer/DuplicatiIndexer.csproj"

# Copy the rest of the source code
COPY ["Indexer/", "Indexer/"]
COPY ["AdapterInterfaces/", "AdapterInterfaces/"]
COPY ["GeminiAdapter/", "GeminiAdapter/"]
COPY ["ClaudeAdapter/", "ClaudeAdapter/"]
COPY ["ChatGPTAdapter/", "ChatGPTAdapter/"]
COPY ["OllamaAdapter/", "OllamaAdapter/"]
# Invalidate docker build caching to pick up code changes for UnstructuredAdapter and Handlers
RUN echo "Cache break 5"
COPY ["UnstructuredAdapter/", "UnstructuredAdapter/"]
COPY ["MarkItDownAdapter/", "MarkItDownAdapter/"]
COPY ["SurrealAdapter/", "SurrealAdapter/"]

# Build and publish the Duplicati CommandLine (duplicati-cli)
WORKDIR "/src/modules/duplicati/Executables/Duplicati.CommandLine"
RUN dotnet publish "Duplicati.CommandLine.csproj" -c Release -o /app/duplicati-cli /p:UseAppHost=true

# Build and publish the Duplicati BackendTool
WORKDIR "/src/modules/duplicati/Executables/Duplicati.CommandLine.BackendTool"
RUN dotnet publish "Duplicati.CommandLine.BackendTool.csproj" -c Release -o /app/duplicati-backend-tool /p:UseAppHost=true

# Build and publish the Indexer
WORKDIR "/src/Indexer"
RUN dotnet publish "DuplicatiIndexer.csproj" -c Release -o /app/publish /p:UseAppHost=false

# Final stage/image
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
# Install Python3, pip, and markitdown early to heavily leverage Docker layer caching
RUN rm -rf /var/lib/apt/lists/* && \
    apt-get clean && \
    apt-get update -o Acquire::AllowInsecureRepositories=true -o Acquire::AllowDowngradeToInsecureRepositories=true && \
    apt-get install -y --no-install-recommends --allow-unauthenticated python3 python3-pip && \
    pip3 install markitdown --break-system-packages && \
    rm -rf /var/lib/apt/lists/*

COPY --from=build /app/publish .
COPY --from=build /app/duplicati-cli /app/duplicati-cli
COPY --from=build /app/duplicati-backend-tool /app/duplicati-backend-tool

# Add Duplicati tools to PATH
RUN ln -s /app/duplicati-cli/Duplicati.CommandLine /usr/local/bin/duplicati-cli && \
    ln -s /app/duplicati-backend-tool/Duplicati.CommandLine.BackendTool /usr/local/bin/duplicati-backend-tool

# Copy Python tools
COPY ["tools/", "/app/tools/"]

# Create directory for SQLite database
RUN mkdir -p /app/data

EXPOSE 8080

ENTRYPOINT ["dotnet", "DuplicatiIndexer.dll"]
