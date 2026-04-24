using DuplicatiIndexer.Data.Entities;
using DuplicatiIndexer.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Indexer.Tests;

public class DiffCalculatorTests
{
    [Fact]
    public void CalculateDiff_ShouldReturnOnlyIndexableFiles_ForCurrentVersion()
    {
        // Arrange
        var backupSourceId = Guid.NewGuid();
        var currentVersion = new DateTime(2024, 3, 12, 14, 30, 0, DateTimeKind.Utc);
        var previousVersion = new DateTime(2024, 3, 11, 10, 0, 0, DateTimeKind.Utc);

        var fileEntries = new List<BackupFileEntry>
        {
            new() { Id = Guid.NewGuid(), BackupSourceId = backupSourceId, VersionAdded = currentVersion, Path = "file1.txt" },
            new() { Id = Guid.NewGuid(), BackupSourceId = backupSourceId, VersionAdded = currentVersion, Path = "file2.exe" },
            new() { Id = Guid.NewGuid(), BackupSourceId = backupSourceId, VersionAdded = previousVersion, Path = "file3.md" },
            new() { Id = Guid.NewGuid(), BackupSourceId = backupSourceId, VersionAdded = currentVersion, VersionDeleted = currentVersion, Path = "file4.pdf" },
            new() { Id = Guid.NewGuid(), BackupSourceId = Guid.NewGuid(), VersionAdded = currentVersion, Path = "file5.docx" }
        };

        var calculator = new DiffCalculator(NullLogger<DiffCalculator>.Instance);

        // Act
        var result = calculator.CalculateDiff(fileEntries, backupSourceId, previousVersion, currentVersion);

        // Assert
        result.Should().NotBeNull();
        result.Should().HaveCount(1);
        result.First().Path.Should().Be("file1.txt");
    }

    [Fact]
    public void CalculateDiff_ShouldReturnEmptyList_WhenNoFilesMatch()
    {
        // Arrange
        var backupSourceId = Guid.NewGuid();
        var currentVersion = new DateTime(2024, 3, 12, 14, 30, 0, DateTimeKind.Utc);
        var previousVersion = new DateTime(2024, 3, 11, 10, 0, 0, DateTimeKind.Utc);

        var fileEntries = new List<BackupFileEntry>
        {
            new() { Id = Guid.NewGuid(), BackupSourceId = backupSourceId, VersionAdded = currentVersion, Path = "file1.exe" },
            new() { Id = Guid.NewGuid(), BackupSourceId = backupSourceId, VersionAdded = previousVersion, Path = "file2.txt" }
        };

        var calculator = new DiffCalculator(NullLogger<DiffCalculator>.Instance);

        // Act
        var result = calculator.CalculateDiff(fileEntries, backupSourceId, previousVersion, currentVersion);

        // Assert
        result.Should().NotBeNull();
        result.Should().BeEmpty();
    }

    [Fact]
    public void CalculateDiff_ShouldIncludePdfFiles()
    {
        // Arrange
        var backupSourceId = Guid.NewGuid();
        var currentVersion = new DateTime(2024, 3, 12, 14, 30, 0, DateTimeKind.Utc);
        var previousVersion = DateTimeOffset.MinValue;

        var fileEntries = new List<BackupFileEntry>
        {
            new() { Id = Guid.NewGuid(), BackupSourceId = backupSourceId, VersionAdded = currentVersion, Path = "document.pdf" }
        };

        var calculator = new DiffCalculator(NullLogger<DiffCalculator>.Instance);

        // Act
        var result = calculator.CalculateDiff(fileEntries, backupSourceId, previousVersion, currentVersion);

        // Assert
        result.Should().HaveCount(1);
        result.First().Path.Should().Be("document.pdf");
    }

    [Fact]
    public void CalculateDiff_ShouldExcludeDeletedFiles()
    {
        // Arrange
        var backupSourceId = Guid.NewGuid();
        var currentVersion = new DateTime(2024, 3, 12, 14, 30, 0, DateTimeKind.Utc);
        var previousVersion = new DateTime(2024, 3, 11, 10, 0, 0, DateTimeKind.Utc);

        var fileEntries = new List<BackupFileEntry>
        {
            new() { Id = Guid.NewGuid(), BackupSourceId = backupSourceId, VersionAdded = currentVersion, VersionDeleted = currentVersion, Path = "deleted.txt" }
        };

        var calculator = new DiffCalculator(NullLogger<DiffCalculator>.Instance);

        // Act
        var result = calculator.CalculateDiff(fileEntries, backupSourceId, previousVersion, currentVersion);

        // Assert
        result.Should().BeEmpty();
    }

    [Fact]
    public void CalculateDiff_ShouldIncludeFilesDeletedInLaterVersion()
    {
        // Arrange
        var backupSourceId = Guid.NewGuid();
        var currentVersion = new DateTime(2024, 3, 12, 14, 30, 0, DateTimeKind.Utc);
        var previousVersion = new DateTime(2024, 3, 11, 10, 0, 0, DateTimeKind.Utc);
        var laterVersion = new DateTime(2024, 3, 13, 10, 0, 0, DateTimeKind.Utc);

        var fileEntries = new List<BackupFileEntry>
        {
            new() { Id = Guid.NewGuid(), BackupSourceId = backupSourceId, VersionAdded = currentVersion, VersionDeleted = laterVersion, Path = "still-existing.txt" }
        };

        var calculator = new DiffCalculator(NullLogger<DiffCalculator>.Instance);

        // Act
        var result = calculator.CalculateDiff(fileEntries, backupSourceId, previousVersion, currentVersion);

        // Assert
        result.Should().HaveCount(1);
        result.First().Path.Should().Be("still-existing.txt");
    }

    [Fact]
    public void CalculateDiff_ShouldIgnoreFilesFromOtherBackupSources()
    {
        // Arrange
        var backupSourceId = Guid.NewGuid();
        var otherBackupSourceId = Guid.NewGuid();
        var currentVersion = new DateTime(2024, 3, 12, 14, 30, 0, DateTimeKind.Utc);
        var previousVersion = DateTimeOffset.MinValue;

        var fileEntries = new List<BackupFileEntry>
        {
            new() { Id = Guid.NewGuid(), BackupSourceId = otherBackupSourceId, VersionAdded = currentVersion, Path = "other-source.txt" }
        };

        var calculator = new DiffCalculator(NullLogger<DiffCalculator>.Instance);

        // Act
        var result = calculator.CalculateDiff(fileEntries, backupSourceId, previousVersion, currentVersion);

        // Assert
        result.Should().BeEmpty();
    }
}
