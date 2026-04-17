using DuplicatiIndexer.AdapterInterfaces;

namespace DuplicatiIndexer.Services;

/// <summary>
/// Implementation of group permission service that provides ACL information
/// for file ingestion and user queries.
/// Supports default group IDs from backup sources with optional path-based overrides.
/// </summary>
public class GroupPermissionService : IGroupPermissionService
{
    private readonly IConfiguration _configuration;

    /// <summary>
    /// Initializes a new instance of the <see cref="GroupPermissionService"/> class.
    /// </summary>
    /// <param name="configuration">The configuration for fallback group settings.</param>
    public GroupPermissionService(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    /// <inheritdoc />
    public IReadOnlyList<string> GetAllowedGroupIds(string filePath, IReadOnlyList<string> defaultGroupIds)
    {
        // Validate inputs
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ArgumentException("File path cannot be null or empty.", nameof(filePath));
        }

        if (defaultGroupIds == null)
        {
            throw new ArgumentNullException(nameof(defaultGroupIds));
        }

        // Validate group IDs - no null or empty strings allowed
        foreach (var groupId in defaultGroupIds)
        {
            if (string.IsNullOrWhiteSpace(groupId))
            {
                throw new ArgumentException("Group IDs cannot contain null or empty values.", nameof(defaultGroupIds));
            }
        }

        // If default groups are provided, use them as the base
        // Otherwise, fall back to configured default groups from settings
        var baseGroups = defaultGroupIds.Count > 0
            ? defaultGroupIds
            : GetConfiguredFallbackGroups();

        // Apply path-based overrides if configured
        // This is where custom logic can be added to override groups based on file path
        var overrideGroups = GetPathBasedOverrideGroups(filePath);

        // If path-based overrides exist, use them; otherwise use the base groups
        return overrideGroups.Count > 0
            ? overrideGroups
            : baseGroups;
    }

    /// <inheritdoc />
    public IReadOnlyList<string> GetCurrentUserGroupIds()
    {
        // TODO: Implement real user group resolution from auth system (claims, headers, etc.)
        // For now, return configured default groups or empty list
        return GetConfiguredFallbackGroups();
    }

    /// <summary>
    /// Gets the configured fallback group IDs from application settings.
    /// </summary>
    /// <returns>A list of fallback group IDs.</returns>
    private IReadOnlyList<string> GetConfiguredFallbackGroups()
    {
        var fallbackGroups = _configuration.GetSection("AclSettings:FallbackGroups")
            .Get<List<string>>();

        return fallbackGroups?.Count > 0
            ? fallbackGroups.AsReadOnly()
            : Array.Empty<string>();
    }

    /// <summary>
    /// Gets path-based override group IDs if configured for the given file path.
    /// This method can be customized to implement path-based ACL rules.
    /// </summary>
    /// <param name="filePath">The file path to check for overrides.</param>
    /// <returns>A list of override group IDs, or empty if no override applies.</returns>
    private static IReadOnlyList<string> GetPathBasedOverrideGroups(string filePath)
    {
        // Example path-based overrides (can be customized or made configurable):
        // if (filePath.Contains("/sensitive/"))
        //     return new[] { "admin", "security" };
        // if (filePath.Contains("/public/"))
        //     return new[] { "group1", "group2", "guest" };

        // For now, no path-based overrides are configured
        return Array.Empty<string>();
    }
}
