using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SurrealDb.Net;

namespace DuplicatiIndexer.Data;

/// <summary>
/// Implementation of ISurrealRepository using SurrealDb.Net.
/// </summary>
public class SurrealRepository : ISurrealRepository
{
    private readonly ISurrealDbClient _client;

    public SurrealRepository(ISurrealDbClient client)
    {
        _client = client;
    }

    private string GetTableForType<T>() => typeof(T).Name.ToLowerInvariant();

    public async Task<T?> GetAsync<T>(Guid id, CancellationToken cancellationToken = default) where T : class
    {
        var recordId = new SurrealDb.Net.Models.StringRecordId($"{GetTableForType<T>()}:{id:N}");
        var result = await _client.Select<T>(recordId, cancellationToken);
        return result;
    }

    public async Task StoreAsync<T>(T entity, CancellationToken cancellationToken = default) where T : class
    {
        var type = typeof(T);
        var idProperty = type.GetProperty("Id");
        if (idProperty == null)
        {
            throw new InvalidOperationException($"Entity of type {type.Name} must have an 'Id' property.");
        }

        var idValue = idProperty.GetValue(entity);
        if (idValue == null)
        {
            throw new InvalidOperationException($"Entity Id cannot be null.");
        }

        Guid id;
        if (idValue is Guid g) id = g;
        else if (idValue is string s && Guid.TryParse(s, out var pg)) id = pg;
        else throw new InvalidOperationException($"Entity Id must be a Guid.");

        var table = GetTableForType<T>();
        var properties = type.GetProperties().Where(p => p.CanRead).ToList();
        
        var updateClauses = properties
            .Where(p => p.Name != "Id")
            .Select(p => $"{p.Name} = $input.{p.Name}");
            
        var updateString = string.Join(", ", updateClauses);
        
        var query = $"INSERT INTO {table} $p0 ON DUPLICATE KEY UPDATE {updateString};";

        var dict = new Dictionary<string, object?>();
        dict["id"] = id.ToString("N");

        foreach (var prop in properties)
        {
            dict[prop.Name] = prop.GetValue(entity);
        }

        var parameters = new Dictionary<string, object?> { { "p0", dict } };
        await _client.RawQuery(query, parameters, cancellationToken);
    }

    public async Task StoreManyAsync<T>(IEnumerable<T> entities, CancellationToken cancellationToken = default) where T : class
    {
        var type = typeof(T);
        var properties = type.GetProperties().Where(p => p.CanRead).ToList();
        var idProperty = properties.FirstOrDefault(p => p.Name == "Id");
        
        if (idProperty == null)
        {
            throw new InvalidOperationException($"Entity of type {type.Name} must have an 'Id' property.");
        }

        var table = GetTableForType<T>();
        var updateClauses = properties
            .Where(p => p.Name != "Id")
            .Select(p => p.PropertyType == typeof(DateTime) ? $"{p.Name} = type::datetime($input.{p.Name})" : $"{p.Name} = $input.{p.Name}");
            
        var updateString = string.Join(", ", updateClauses);
        
        var query = $"INSERT INTO {table} $p0 ON DUPLICATE KEY UPDATE {updateString};";

        var chunks = entities.Chunk(1000).ToList();

        foreach (var chunk in chunks)
        {
            var items = new List<Dictionary<string, object?>>(chunk.Length);

            for (int i = 0; i < chunk.Length; i++)
            {
                var entity = chunk[i];
                var idValue = idProperty.GetValue(entity);
                if (idValue == null) throw new InvalidOperationException("Entity Id cannot be null.");

                Guid id;
                if (idValue is Guid g) id = g;
                else if (idValue is string s && Guid.TryParse(s, out var pg)) id = pg;
                else throw new InvalidOperationException("Entity Id must be a Guid.");

                var dict = new Dictionary<string, object?>();
                dict["id"] = id.ToString("N");

                foreach (var prop in properties)
                {
                    var val = prop.GetValue(entity);
                    if (val is DateTime dt)
                    {
                        dict[prop.Name] = dt.ToString("O");
                    }
                    else
                    {
                        dict[prop.Name] = val;
                    }
                }

                items.Add(dict);
            }

            var parameters = new Dictionary<string, object?> { { "p0", items } };
            await _client.RawQuery(query, parameters, cancellationToken);
        }
    }

    public async Task DeleteAsync<T>(Guid id, CancellationToken cancellationToken = default) where T : class
    {
        var recordId = new SurrealDb.Net.Models.StringRecordId($"{GetTableForType<T>()}:{id:N}");
        await _client.Delete(recordId, cancellationToken);
    }

    public async Task<IReadOnlyList<T>> QueryAsync<T>(string query, IReadOnlyDictionary<string, object>? parameters = null, CancellationToken cancellationToken = default)
    {
        var response = await _client.RawQuery(query, parameters?.ToDictionary(k => k.Key, v => (object?)v.Value));
        var result = response.GetValue<List<T>>(0);
        return result ?? new List<T>();
    }

    public async Task<T?> QueryScalarAsync<T>(string query, IReadOnlyDictionary<string, object>? parameters = null, CancellationToken cancellationToken = default)
    {
        var response = await _client.RawQuery(query, parameters?.ToDictionary(k => k.Key, v => (object?)v.Value));
        var result = response.GetValue<List<T>>(0);
        return result != null && result.Count > 0 ? result.First() : default;
    }
}
