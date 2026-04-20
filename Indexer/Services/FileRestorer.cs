using System.Diagnostics;
using System.Text;
using DuplicatiIndexer.Data.Entities;

namespace DuplicatiIndexer.Services;

/// <summary>
/// Service for restoring files from Duplicati backups using the Duplicati CLI.
/// </summary>
public class FileRestorer
{
    private readonly ILogger<FileRestorer> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="FileRestorer"/> class.
    /// </summary>
    /// <param name="logger">The logger instance.</param>
    public FileRestorer(ILogger<FileRestorer> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Restores files from a Duplicati backup to a target directory.
    /// </summary>
    /// <param name="filesToRestore">List of file entries to restore.</param>
    /// <param name="targetDirectory">The directory where files will be restored.</param>
    /// <param name="backendUrl">The Duplicati backend URL.</param>
    /// <param name="passphrase">The encryption passphrase (if applicable).</param>
    /// <param name="time">The backup time to restore from (ISO 8601 format).</param>
    /// <returns>True if restoration was successful, otherwise false.</returns>
    /// <exception cref="ArgumentException">Thrown when target directory or backend URL is null or empty.</exception>
    public async Task<bool> RestoreFilesAsync(
        List<string> filePaths,
        string targetDirectory,
        string backendUrl,
        string? passphrase,
        string time)
    {
        if (filePaths == null || !filePaths.Any())
        {
            _logger.LogInformation("No files to restore.");
            return true;
        }

        if (string.IsNullOrWhiteSpace(targetDirectory))
            throw new ArgumentException("Target directory cannot be null or empty.", nameof(targetDirectory));

        if (string.IsNullOrWhiteSpace(backendUrl))
            throw new ArgumentException("Backend URL cannot be null or empty.", nameof(backendUrl));

        _logger.LogInformation("Restoring {Count} files to {TargetDirectory} from time {Time}",
            filePaths.Count, targetDirectory, time);

        // Ensure target directory exists
        if (!Directory.Exists(targetDirectory))
        {
            Directory.CreateDirectory(targetDirectory);
        }

        // Build command arguments
        var args = new List<string> { "restore", backendUrl };

        // Add files to restore
        args.AddRange(filePaths);

        // Add options
        args.Add($"--restore-path={targetDirectory}");
        args.Add($"--no-local-db");
        args.Add($"--dont-compress-restore-paths");

        if (!string.IsNullOrWhiteSpace(passphrase))
            args.Add($"--passphrase={passphrase}");
        else
            args.Add("--no-encryption");

        args.Add($"--time={time}");

        // Execute Duplicati CLI
        _logger.LogDebug("Executing Duplicati CLI with arguments: {Args}", string.Join(" ", args));

        var resolved = DuplicatiPathResolver.FindCommandLine();
        var processStartInfo = resolved.CreateProcessStartInfo(args.ToArray());

        _logger.LogDebug("Executing Duplicati CLI: {FileName} with {ArgCount} arguments",
            processStartInfo.FileName,
            processStartInfo.ArgumentList.Count);

        using var process = new Process { StartInfo = processStartInfo };
        var outputBuilder = new StringBuilder();
        var errorBuilder = new StringBuilder();

        process.OutputDataReceived += (sender, e) =>
        {
            if (e.Data != null)
            {
                outputBuilder.AppendLine(e.Data);
                _logger.LogTrace("Duplicati Output: {Data}", e.Data);
            }
        };

        process.ErrorDataReceived += (sender, e) =>
        {
            if (e.Data != null)
            {
                errorBuilder.AppendLine(e.Data);
                _logger.LogWarning("Duplicati Error: {Data}", e.Data);
            }
        };

        try
        {
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            await process.WaitForExitAsync();

            if (process.ExitCode != 0 && process.ExitCode != 2) // Returns 2 on warnings
            {
                _logger.LogError("Duplicati restore failed with exit code {ExitCode}. Error: {Error}. Output: {Output}",
                    process.ExitCode, errorBuilder.ToString(), outputBuilder.ToString());
                return false;
            }

            _logger.LogDebug("Duplicati restore completed successfully ({ExitCode}). Output: {Output}", process.ExitCode, outputBuilder.ToString());

            _logger.LogInformation("Successfully restored {Count} files.", filePaths.Count);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "An error occurred while executing the Duplicati restore process.");
            return false;
        }
    }

}
