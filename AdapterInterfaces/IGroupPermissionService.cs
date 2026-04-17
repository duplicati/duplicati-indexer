namespace DuplicatiIndexer.AdapterInterfaces;

/// <summary>
/// Service for obtaining group permissions for files during ingestion and querying.
/// Supports default group IDs from backup sources with optional path-based overrides.
/// </summary>
public interface IGroupPermissionService
{
    /// <summary>
    /// Gets the list of allowed group IDs for a given file path during ingestion.
    /// This method uses the default group IDs as a base and applies path-based overrides if configured.
    /// </summary>
    /// <param name="filePath">The path of the file being ingested.</param>
    /// <param name="defaultGroupIds">The default group IDs from the backup source.</param>
    /// <returns>A list of group IDs that are allowed to access this file.</returns>
    IReadOnlyList<string> GetAllowedGroupIds(string filePath, IReadOnlyList<string> defaultGroupIds);

    /// <summary>
    /// Gets the list of group IDs for the current user during querying.
    /// </summary>
    /// <returns>A list of group IDs that the current user belongs to.</returns>
    IReadOnlyList<string> GetCurrentUserGroupIds();
}
