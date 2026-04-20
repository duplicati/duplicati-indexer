using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DuplicatiIndexer.Data;

/// <summary>
/// A repository interface to manage document storage using SurrealDB.
/// Replaces the Marten IDocumentSession.
/// </summary>
public interface ISurrealRepository
{
    /// <summary>
    /// Gets a document by its ID.
    /// </summary>
    Task<T?> GetAsync<T>(Guid id, CancellationToken cancellationToken = default) where T : class;

    /// <summary>
    /// Stores or updates a document.
    /// </summary>
    Task StoreAsync<T>(T entity, CancellationToken cancellationToken = default) where T : class;

    /// <summary>
    /// Stores or updates multiple documents.
    /// </summary>
    Task StoreManyAsync<T>(IEnumerable<T> entities, CancellationToken cancellationToken = default) where T : class;

    /// <summary>
    /// Deletes a document by ID.
    /// </summary>
    Task DeleteAsync<T>(Guid id, CancellationToken cancellationToken = default) where T : class;

    /// <summary>
    /// Executes a custom SurrealQL query.
    /// </summary>
    Task<IReadOnlyList<T>> QueryAsync<T>(string query, IReadOnlyDictionary<string, object>? parameters = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Executes a custom SurrealQL scalar query.
    /// </summary>
    Task<T?> QueryScalarAsync<T>(string query, IReadOnlyDictionary<string, object>? parameters = null, CancellationToken cancellationToken = default);
}
