namespace Indexer.IntegrationTests.Fixtures;

/// <summary>
/// Collection definition for integration tests that use Docker containers.
/// </summary>
[CollectionDefinition("IntegrationTests")]
public class IntegrationTestCollection : ICollectionFixture<SurrealDbFixture>
{
}
