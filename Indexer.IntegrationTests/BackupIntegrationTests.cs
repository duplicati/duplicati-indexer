using DuplicatiIndexer.Data;
using DuplicatiIndexer.Data.Entities;
using DuplicatiIndexer.Services;
using FluentAssertions;
using Indexer.IntegrationTests.Fixtures;
using Indexer.IntegrationTests.Helpers;
using SurrealDb.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit.Abstractions;

namespace Indexer.IntegrationTests;

/// <summary>
/// Integration tests for backup operations using Docker containers.
/// </summary>
[Collection("IntegrationTests")]
public class BackupIntegrationTests : IDisposable
{
    private readonly SurrealDbFixture _surrealDbFixture;
        private readonly ITestOutputHelper _output;
    private readonly DuplicatiCliHelper _duplicatiCli;
    private readonly string _testDirectory;
    private readonly string _backupDirectory;
    private readonly ServiceProvider _serviceProvider;

    public BackupIntegrationTests(SurrealDbFixture surrealDbFixture, ITestOutputHelper output)
    {
        _surrealDbFixture = surrealDbFixture;
        _output = output;
        _duplicatiCli = new DuplicatiCliHelper(output);

        // Create test directories
        _testDirectory = Path.Combine(Path.GetTempPath(), $"duplicati_test_{Guid.NewGuid():N}");
        _backupDirectory = Path.Combine(_testDirectory, "backups");
        Directory.CreateDirectory(_testDirectory);
        Directory.CreateDirectory(_backupDirectory);

        _output.WriteLine($"Test directory: {_testDirectory}");
        _output.WriteLine($"Backup directory: {_backupDirectory}");

        // Setup DI container with test configuration
        var services = new ServiceCollection();
        
        // Configure logging
        services.AddLogging(builder => builder.AddXUnit(output));

        // Configure SurrealDB
        services.AddSurreal(_surrealDbFixture.ConnectionUrl);
        services.AddScoped<ISurrealRepository, SurrealRepository>();
        
        // Initialize logic for sign-in
        var baseProvider = services.BuildServiceProvider();
        var client = baseProvider.GetRequiredService<ISurrealDbClient>();
        client.SignIn(new SurrealDb.Net.Models.Auth.RootAuth { Username = "root", Password = "root" }).GetAwaiter().GetResult();
        client.Use("test", "test").GetAwaiter().GetResult();

        // Register services
        services.AddScoped<DlistProcessor>();
        services.AddScoped<DiffCalculator>();

        _serviceProvider = services.BuildServiceProvider();
    }

    public void Dispose()
    {
        // Cleanup test directories
        try
        {
            if (Directory.Exists(_testDirectory))
            {
                Directory.Delete(_testDirectory, recursive: true);
            }
        }
        catch (Exception ex)
        {
            _output.WriteLine($"Failed to cleanup test directory: {ex.Message}");
        }

        _serviceProvider.Dispose();
    }

    /// <summary>
    /// Creates a test BackupSource in the database.
    /// </summary>
    private async Task<BackupSource> CreateTestBackupSource(string backupId, string targetUrl, string? encryptionPassword = null)
    {
        using var scope = _serviceProvider.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<ISurrealRepository>();

        var backupSource = new BackupSource
        {
            Id = Guid.NewGuid(),
            DuplicatiBackupId = backupId,
            Name = $"Test Backup {backupId}",
            CreatedAt = DateTimeOffset.UtcNow,
            TargetUrl = targetUrl,
            EncryptionPassword = encryptionPassword
        };

        await repository.StoreAsync(backupSource);

        _output.WriteLine($"Created BackupSource: {backupSource.Id} for backupId: {backupId}");
        return backupSource;
    }

    /// <summary>
    /// Tests creating a backup with data, indexing it, making a modification, and creating a new backup.
    /// Verifies that the diff calculator correctly identifies changed files.
    /// </summary>
    [Fact]
    public async Task BackupCreate_Modify_CreateNewBackup_DiffCalculator_FindsChanges()
    {
        // Arrange: Create initial test data
        var sourceDir = Path.Combine(_testDirectory, "source");
        Directory.CreateDirectory(sourceDir);

        // Create some test files
        var file1Path = Path.Combine(sourceDir, "document1.txt");
        var file2Path = Path.Combine(sourceDir, "document2.txt");
        var file3Path = Path.Combine(sourceDir, "notes.md");

        await File.WriteAllTextAsync(file1Path, "This is the content of document 1.");
        await File.WriteAllTextAsync(file2Path, "This is the content of document 2.");
        await File.WriteAllTextAsync(file3Path, "# Notes\n\nThese are my notes.");

        _output.WriteLine("Created initial test files:");
        _output.WriteLine($"  - {file1Path}");
        _output.WriteLine($"  - {file2Path}");
        _output.WriteLine($"  - {file3Path}");

        // Create first backup
        var backupTarget = $"file://{_backupDirectory}";
        var passphrase = "test-passphrase-123";
        var backupId = "integration-test-backup";

        // Create BackupSource first (required before processing dlist)
        await CreateTestBackupSource(backupId, backupTarget, passphrase);

        _output.WriteLine("Creating first backup...");
        var duplicatiVersion1 = await _duplicatiCli.BackupAsync(sourceDir, backupTarget, passphrase);
        _output.WriteLine($"First backup completed. Duplicati version: {duplicatiVersion1}");

        // Get the dlist file path and extract version timestamp
        var dlistFile1 = _duplicatiCli.GetDlistFilePath(backupTarget, duplicatiVersion1);
        var version1 = DuplicatiCliHelper.ExtractVersionFromDlistPath(dlistFile1);
        _output.WriteLine($"Dlist file for version 1: {dlistFile1}, Extracted timestamp: {version1}");

        // Act: Process the first backup dlist
        using (var scope = _serviceProvider.CreateScope())
        {
            var processor = scope.ServiceProvider.GetRequiredService<DlistProcessor>();

            _output.WriteLine("Processing first backup dlist file...");
            await processor.ProcessDlistAsync(backupId, version1, dlistFile1, passphrase);

            // Verify files were stored in database
            var repository = scope.ServiceProvider.GetRequiredService<ISurrealRepository>();
            var backupSource = await repository.QueryScalarAsync<BackupSource>("SELECT * FROM BackupSource WHERE DuplicatiBackupId = $id", new Dictionary<string, object> { ["id"] = backupId });

            backupSource.Should().NotBeNull();
            backupSource!.LastParsedVersion.Should().Be(version1);

            var fileEntries = await repository.QueryAsync<BackupFileEntry>("SELECT * FROM BackupFileEntry WHERE BackupSourceId = $id", new Dictionary<string, object> { ["id"] = backupSource!.Id });

            fileEntries.Should().HaveCount(3);
            fileEntries.Should().Contain(f => f.Path.Contains("document1.txt"));
            fileEntries.Should().Contain(f => f.Path.Contains("document2.txt"));
            fileEntries.Should().Contain(f => f.Path.Contains("notes.md"));

            _output.WriteLine($"Verified {fileEntries.Count} files stored in database");
        }

        // Arrange: Modify files for second backup
        _output.WriteLine("Modifying files for second backup...");
        await File.WriteAllTextAsync(file1Path, "This is the UPDATED content of document 1.");
        await File.WriteAllTextAsync(file3Path, "# Updated Notes\n\nThese are my updated notes.\n\nAdded more content here.");

        // Delete file2
        File.Delete(file2Path);
        _output.WriteLine("Deleted document2.txt");

        // Create second backup
        _output.WriteLine("Creating second backup...");
        var duplicatiVersion2 = await _duplicatiCli.BackupAsync(sourceDir, backupTarget, passphrase);
        _output.WriteLine($"Second backup completed. Duplicati version: {duplicatiVersion2}");

        // Get the dlist file path and extract version timestamp
        var dlistFile2 = _duplicatiCli.GetDlistFilePath(backupTarget, duplicatiVersion2);
        var version2 = DuplicatiCliHelper.ExtractVersionFromDlistPath(dlistFile2);
        _output.WriteLine($"Dlist file for version 2: {dlistFile2}, Extracted timestamp: {version2}");

        // Act: Process the second backup dlist
        using (var scope = _serviceProvider.CreateScope())
        {
            var processor = scope.ServiceProvider.GetRequiredService<DlistProcessor>();

            _output.WriteLine("Processing second backup dlist file...");
            await processor.ProcessDlistAsync(backupId, version2, dlistFile2, passphrase);

            // Verify backup source updated
            var repository = scope.ServiceProvider.GetRequiredService<ISurrealRepository>();
            var backupSource = await repository.QueryScalarAsync<BackupSource>("SELECT * FROM BackupSource WHERE DuplicatiBackupId = $id", new Dictionary<string, object> { ["id"] = backupId });

            backupSource.Should().NotBeNull();
            backupSource!.LastParsedVersion.Should().Be(version2);

            _output.WriteLine($"Backup source LastParsedVersion updated to {version2}");
        }

        // Act: Calculate diff between versions
        using (var scope = _serviceProvider.CreateScope())
        {
            var diffCalculator = scope.ServiceProvider.GetRequiredService<DiffCalculator>();
            var repository = scope.ServiceProvider.GetRequiredService<ISurrealRepository>();

            var backupSource = await repository.QueryScalarAsync<BackupSource>("SELECT * FROM BackupSource WHERE DuplicatiBackupId = 'integration-test-backup'");

            backupSource.Should().NotBeNull();

            // Query all file entries for this backup source
            var entries = await repository.QueryAsync<BackupFileEntry>("SELECT * FROM BackupFileEntry WHERE BackupSourceId = $id", new Dictionary<string, object> { ["id"] = backupSource!.Id });

            _output.WriteLine("Calculating diff between versions...");
            var changedFiles = diffCalculator.CalculateDiff(
                entries, backupSource!.Id, version1, version2);

            // Assert: Verify diff results
            _output.WriteLine($"Found {changedFiles.Count} changed files");
            foreach (var file in changedFiles)
            {
                _output.WriteLine($"  - {file.Path} (VersionAdded: {file.VersionAdded})");
            }

            // Should have 2 changed files (document1.txt and notes.md - both indexable)
            // document2.txt was deleted but it's not in version2 so it won't show as changed
            changedFiles.Should().HaveCount(2);
            changedFiles.Should().Contain(f => f.Path.Contains("document1.txt"));
            changedFiles.Should().Contain(f => f.Path.Contains("notes.md"));
        }

        // Verify all file entries in database
        using (var scope = _serviceProvider.CreateScope())
        {
            var repository = scope.ServiceProvider.GetRequiredService<ISurrealRepository>();
            var backupSource = await repository.QueryScalarAsync<BackupSource>("SELECT * FROM BackupSource WHERE DuplicatiBackupId = 'integration-test-backup'");

            var allEntries = await repository.QueryAsync<BackupFileEntry>("SELECT * FROM BackupFileEntry WHERE BackupSourceId = $id", new Dictionary<string, object> { ["id"] = backupSource!.Id });

            _output.WriteLine($"\nAll file entries in database: {allEntries.Count}");
            foreach (var entry in allEntries.OrderBy(e => e.Path))
            {
                _output.WriteLine($"  - {entry.Path}: Added in v{entry.VersionAdded}, Deleted: {entry.VersionDeleted?.ToString() ?? "N/A"}");
            }

            // Should have entries for all versions
            allEntries.Count.Should().BeGreaterThanOrEqualTo(3);
        }
    }

    /// <summary>
    /// Tests that the DlistProcessor correctly handles encrypted backup files.
    /// </summary>
    [Fact]
    public async Task DlistProcessor_ProcessesEncryptedBackup_Correctly()
    {
        // Arrange
        var sourceDir = Path.Combine(_testDirectory, "encrypted_source");
        Directory.CreateDirectory(sourceDir);

        var testFile = Path.Combine(sourceDir, "secret.txt");
        await File.WriteAllTextAsync(testFile, "This is secret content that should be encrypted.");

        var backupTarget = $"file://{_backupDirectory}/encrypted";
        Directory.CreateDirectory(backupTarget.Replace("file://", ""));
        var passphrase = "my-secret-passphrase-456";
        var backupId = $"encrypted-test-{Guid.NewGuid():N}";

        // Create BackupSource first
        await CreateTestBackupSource(backupId, backupTarget, passphrase);

        _output.WriteLine("Creating encrypted backup...");
        var duplicatiVersion = await _duplicatiCli.BackupAsync(sourceDir, backupTarget, passphrase);

        var dlistFile = _duplicatiCli.GetDlistFilePath(backupTarget, duplicatiVersion);
        var version = DuplicatiCliHelper.ExtractVersionFromDlistPath(dlistFile);
        _output.WriteLine($"Dlist file: {dlistFile}, Extracted timestamp: {version}");

        // Verify the file is encrypted (should end with .aes)
        if (!dlistFile.EndsWith(".aes"))
        {
            _output.WriteLine("Warning: Backup file is not encrypted as expected");
        }

        // Act
        using var scope = _serviceProvider.CreateScope();
        var processor = scope.ServiceProvider.GetRequiredService<DlistProcessor>();

        _output.WriteLine("Processing encrypted dlist file...");
        await processor.ProcessDlistAsync(backupId, version, dlistFile, passphrase);

        // Assert
        var repository = scope.ServiceProvider.GetRequiredService<ISurrealRepository>();
        var backupSource = await repository.QueryScalarAsync<BackupSource>("SELECT * FROM BackupSource WHERE DuplicatiBackupId = $id", new Dictionary<string, object> { ["id"] = backupId });

        backupSource.Should().NotBeNull();

        var fileEntries = (await repository.QueryAsync<BackupFileEntry>("SELECT * FROM BackupFileEntry WHERE BackupSourceId = $id", new Dictionary<string, object> { ["id"] = backupSource!.Id })).ToList();

        fileEntries.Should().HaveCount(1);
        fileEntries.First().Path.Should().Contain("secret.txt");

        _output.WriteLine($"Successfully processed encrypted backup with {fileEntries.Count} files");
    }

    /// <summary>
    /// Tests the diff calculator with multiple versions to ensure proper change detection.
    /// </summary>
    [Fact]
    public async Task DiffCalculator_WithMultipleVersions_IdentifiesCorrectChanges()
    {
        // Arrange: Create three versions with different changes
        var sourceDir = Path.Combine(_testDirectory, "multi_version");
        Directory.CreateDirectory(sourceDir);

        var backupTarget = $"file://{_backupDirectory}/multi";
        Directory.CreateDirectory(backupTarget.Replace("file://", ""));
        var passphrase = "multi-test-pass";
        var backupId = "multi-version-test";

        // Create BackupSource first
        await CreateTestBackupSource(backupId, backupTarget, passphrase);

        // Version 1: Initial files
        await File.WriteAllTextAsync(Path.Combine(sourceDir, "v1_file.txt"), "Version 1 content");
        await File.WriteAllTextAsync(Path.Combine(sourceDir, "shared.txt"), "Shared content v1");

        _output.WriteLine("Creating version 1...");
        var duplicatiVersion1 = await _duplicatiCli.BackupAsync(sourceDir, backupTarget, passphrase);

        // Version 2: Modify shared, add new file
        await File.WriteAllTextAsync(Path.Combine(sourceDir, "shared.txt"), "Shared content v2");
        await File.WriteAllTextAsync(Path.Combine(sourceDir, "v2_file.txt"), "Version 2 content");

        _output.WriteLine("Creating version 2...");
        var duplicatiVersion2 = await _duplicatiCli.BackupAsync(sourceDir, backupTarget, passphrase);

        // Version 3: Modify shared again, delete v1_file
        await File.WriteAllTextAsync(Path.Combine(sourceDir, "shared.txt"), "Shared content v3");
        File.Delete(Path.Combine(sourceDir, "v1_file.txt"));

        _output.WriteLine("Creating version 3...");
        var duplicatiVersion3 = await _duplicatiCli.BackupAsync(sourceDir, backupTarget, passphrase);

        // Get actual version numbers from Duplicati
        var duplicatiVersions = await _duplicatiCli.ListVersionsAsync(backupTarget, passphrase);
        duplicatiVersions.Count.Should().BeGreaterThanOrEqualTo(3);

        // Get version timestamps from dlist filenames
        var dlistFile1 = _duplicatiCli.GetDlistFilePath(backupTarget, duplicatiVersions[^3]);
        var dlistFile2 = _duplicatiCli.GetDlistFilePath(backupTarget, duplicatiVersions[^2]);
        var dlistFile3 = _duplicatiCli.GetDlistFilePath(backupTarget, duplicatiVersions[^1]);

        var version1 = DuplicatiCliHelper.ExtractVersionFromDlistPath(dlistFile1);
        var version2 = DuplicatiCliHelper.ExtractVersionFromDlistPath(dlistFile2);
        var version3 = DuplicatiCliHelper.ExtractVersionFromDlistPath(dlistFile3);

        _output.WriteLine($"Extracted timestamps: v1={version1}, v2={version2}, v3={version3}");

        // Process all dlists
        using (var scope = _serviceProvider.CreateScope())
        {
            var processor = scope.ServiceProvider.GetRequiredService<DlistProcessor>();

            var dlistFiles = new[] { dlistFile1, dlistFile2, dlistFile3 };
            var versionTimestamps = new[] { version1, version2, version3 };

            for (int i = 0; i < dlistFiles.Length; i++)
            {
                _output.WriteLine($"Processing version {versionTimestamps[i]}: {dlistFiles[i]}");
                await processor.ProcessDlistAsync(backupId, versionTimestamps[i], dlistFiles[i], passphrase);
            }
        }

        // Act & Assert: Calculate diffs between versions
        using (var scope = _serviceProvider.CreateScope())
        {
            var diffCalculator = scope.ServiceProvider.GetRequiredService<DiffCalculator>();
            var repository = scope.ServiceProvider.GetRequiredService<ISurrealRepository>();

            var backupSource = await repository.QueryScalarAsync<BackupSource>("SELECT * FROM BackupSource WHERE DuplicatiBackupId = 'multi-version-test'");

            backupSource.Should().NotBeNull();

            // Diff v1 -> v2
            _output.WriteLine("\nCalculating diff v1 -> v2...");
            var entriesV1V2 = await repository.QueryAsync<BackupFileEntry>("SELECT * FROM BackupFileEntry WHERE BackupSourceId = $id", new Dictionary<string, object> { ["id"] = backupSource!.Id });
            var diffV1V2 = diffCalculator.CalculateDiff(entriesV1V2, backupSource!.Id, version1, version2);
            _output.WriteLine($"Changes v1->v2: {diffV1V2.Count} files");
            foreach (var f in diffV1V2)
            {
                _output.WriteLine($"  - {f.Path}");
            }
            diffV1V2.Should().HaveCount(2); // shared.txt (modified) + v2_file.txt (added)
            diffV1V2.Should().Contain(f => f.Path.Contains("shared.txt"));
            diffV1V2.Should().Contain(f => f.Path.Contains("v2_file.txt"));

            // Diff v2 -> v3
            _output.WriteLine("\nCalculating diff v2 -> v3...");
            var entriesV2V3 = await repository.QueryAsync<BackupFileEntry>("SELECT * FROM BackupFileEntry WHERE BackupSourceId = $id", new Dictionary<string, object> { ["id"] = backupSource.Id });
            var diffV2V3 = diffCalculator.CalculateDiff(entriesV2V3, backupSource.Id, version2, version3);
            _output.WriteLine($"Changes v2->v3: {diffV2V3.Count} files");
            foreach (var f in diffV2V3)
            {
                _output.WriteLine($"  - {f.Path}");
            }
            diffV2V3.Should().Contain(f => f.Path.Contains("shared.txt"));
        }
    }
}
