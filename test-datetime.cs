using System;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using SurrealDb.Net;
using DuplicatiIndexer.Data;
using DuplicatiIndexer.Data.Entities;
class Program {
    static async Task Main() {
        var services = new ServiceCollection();
        services.AddSurreal("http://localhost:8000/rpc", "test", "test", "root", "root");
        var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<ISurrealDbClient>();
        await client.Connect();
        var repo = new SurrealRepository(client);
        var session = new QuerySession { Id = Guid.NewGuid(), Title = "Test Session", CreatedAt = DateTimeOffset.UtcNow, LastActivityAt = DateTimeOffset.UtcNow };
        await repo.StoreAsync(session);
        Console.WriteLine($"Stored session {session.Id}");
    }
}
