# Contributing to Duplicati Indexer

Thank you for your interest in contributing to Duplicati Indexer! This document provides guidelines for contributing to the project.

## Getting Started

1. **Fork the repository** on GitHub
2. **Clone your fork** with submodules:
   ```bash
   git clone --recursive git@github.com:<your-username>/DuplicatiIndexer.git
   ```
3. **Set up the development environment**:
   ```bash
   # Start dependencies
   docker-compose up -d postgres qdrant unstructured ollama
   ```

## Development Setup

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- [Docker](https://docs.docker.com/get-docker/) and Docker Compose
- Git with submodule support

### Building the Project

```bash
# Build the entire solution
dotnet build

# Run the Indexer service locally
cd Indexer
dotnet run
```

### Configuration

Create a local `appsettings.Development.json` or set environment variables:

```bash
export LLM_PROVIDER=Gemini
export GEMINI_API_KEY=<your-api-key>
```

See [README.md](./README.md) for all configuration options.

## Code Style

- Follow existing C# conventions in the codebase
- Use meaningful variable and method names
- Add XML documentation for public APIs
- Keep methods focused and concise
- Use `var` when the type is obvious
- Prefer `async`/`await` over blocking calls

## Testing

All contributions should include tests:

```bash
# Run all tests
dotnet test

# Run with verbose output
dotnet test --logger "console;verbosity=detailed"
```

- **Unit tests**: Use xUnit, Moq, and FluentAssertions (see `Indexer.Tests/`)
- **Integration tests**: Ensure Docker dependencies are running (see `Indexer.IntegrationTests/`)

## Pull Request Process

1. **Create a feature branch** from `main`:
   ```bash
   git checkout -b feature/your-feature-name
   ```

2. **Make your changes** with clear, focused commits

3. **Ensure tests pass**:
   ```bash
   dotnet test
   ```

4. **Update documentation** if needed (README, API docs, etc.)

5. **Submit a pull request** with:
   - Clear description of changes
   - Link to related issue(s)
   - Screenshots for UI changes
   - Notes on breaking changes

## Commit Guidelines

- Use present tense ("Add feature" not "Added feature")
- Use imperative mood ("Move cursor to..." not "Moves cursor to...")
- Keep the first line under 72 characters
- Reference issues and PRs where appropriate

## Reporting Issues

When reporting bugs, please include:

- **Description**: Clear explanation of the issue
- **Steps to reproduce**: Minimal steps to trigger the bug
- **Expected behavior**: What you expected to happen
- **Actual behavior**: What actually happened
- **Environment**: OS, .NET version, Docker version
- **Logs**: Relevant error messages or stack traces

## Feature Requests

Feature requests are welcome! Please:

- Search existing issues first
- Describe the use case clearly
- Explain why the feature would be valuable
- Consider implementation complexity

## Areas for Contribution

Looking for ways to help? Consider:

- **Documentation**: Improve README, add examples, fix typos
- **Bug fixes**: Check the issue tracker for labeled bugs
- **Testing**: Add more unit/integration tests
- **Adapters**: Add support for new LLM providers or vector stores
- **Performance**: Optimize indexing or query performance
- **Security**: Enhance ransomware detection or access controls
- **Frontend**: Improve the Angular web UI

## Questions?

Feel free to open an issue for:
- Questions about the codebase
- Help with development setup
- Discussion of potential features

## License

By contributing, you agree that your contributions will be licensed under the same license as the project.
