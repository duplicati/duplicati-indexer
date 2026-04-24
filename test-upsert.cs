using System;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using SurrealDb.Net;
using DuplicatiIndexer.Data.Entities;
class Program {
    static async Task Main() {
        var services = new ServiceCollection();
        services.AddSurreal("http://localhost:8000/rpc", "test", "test", "root", "root");
        var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<ISurrealDbClient>();
        await client.Connect();
        var session = new QuerySession { Id = Guid.NewGuid(), Title = "Test Upsert", CreatedAt = DateTime.UtcNow, LastActivityAt = DateTime.UtcNow };
        var idStr = session.Id.ToString("N");
        var recordId = new SurrealDb.Net.Models.StringRecordId($"querysession:{idStr}");
        await client.Upsert<QuerySession>(recordId, session);
        Console.WriteLine($"Stored session {session.Id}");
    }
}
