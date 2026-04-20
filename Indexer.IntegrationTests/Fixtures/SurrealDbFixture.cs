using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

namespace Indexer.IntegrationTests.Fixtures;

/// <summary>
/// Fixture that provides a SurrealDB container for integration tests.
/// </summary>
public class SurrealDbFixture : IAsyncLifetime
{
    private readonly IContainer _surrealContainer;

    /// <summary>
    /// Gets the connection URL to the SurrealDB database.
    /// </summary>
    public string ConnectionUrl => $"http://127.0.0.1:{_surrealContainer.GetMappedPublicPort(8000)}/sql";

    public SurrealDbFixture()
    {
        _surrealContainer = new ContainerBuilder()
            .WithImage("surrealdb/surrealdb:latest")
            .WithPortBinding(8000, true)
            .WithCommand("start", "memory", "-A", "--auth", "--user", "root", "--pass", "root")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilPortIsAvailable(8000))
            .Build();
    }

    /// <summary>
    /// Starts the container.
    /// </summary>
    public async Task InitializeAsync()
    {
        await _surrealContainer.StartAsync();
    }

    /// <summary>
    /// Stops and disposes the container.
    /// </summary>
    public async Task DisposeAsync()
    {
        await _surrealContainer.DisposeAsync();
    }
}
