using SurrealDb.Net;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace DuplicatiIndexer.HealthChecks;

/// <summary>
/// Health check for SurrealDB connectivity.
/// </summary>
public class DatabaseHealthCheck : IHealthCheck
{
    private readonly ISurrealDbClient _client;
    private readonly ILogger<DatabaseHealthCheck> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="DatabaseHealthCheck"/> class.
    /// </summary>
    /// <param name="client">The SurrealDB client.</param>
    /// <param name="logger">The logger.</param>
    public DatabaseHealthCheck(ISurrealDbClient client, ILogger<DatabaseHealthCheck> logger)
    {
        _client = client;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await _client.Version(cancellationToken);
            if (result != null)
            {
                return HealthCheckResult.Healthy($"Database is reachable: {result}");
            }
            return HealthCheckResult.Unhealthy("Database is reachable but returned empty version");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Database health check failed");
            return HealthCheckResult.Unhealthy("Database is not reachable", ex);
        }
    }
}
