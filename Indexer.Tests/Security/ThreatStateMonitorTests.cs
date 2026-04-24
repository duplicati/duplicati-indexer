using System.Linq;
using System;
using System.Collections.Generic;
using DuplicatiIndexer.Data.Entities;
using DuplicatiIndexer.Services.Security;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Indexer.Tests.Security;

public class ThreatStateMonitorTests
{
    private readonly ThreatStateMonitor _sut;

    public ThreatStateMonitorTests()
    {
        _sut = new ThreatStateMonitor(NullLogger<ThreatStateMonitor>.Instance);
    }

    [Fact]
    public void CheckForCanaryFiles_NoCanary_ReturnsFalse()
    {
        var files = new List<BackupFileEntry>
        {
            new() { Path = "/docs/normal_file.txt" },
            new() { Path = "/images/photo.png" }
        };

        var result = _sut.CheckForCanaryFiles(files.Select(f => f.Path));
        result.Should().BeFalse();
    }

    [Theory]
    [InlineData("/docs/._canary.docx")]
    [InlineData("/secret/.tripwire.txt")]
    [InlineData("C:\\backups\\_canary_file.pdf")]
    public void CheckForCanaryFiles_IncludesCanary_ReturnsTrue(string canaryPath)
    {
        var files = new List<BackupFileEntry>
        {
            new() { Path = "/docs/normal_file.txt" },
            new() { Path = canaryPath }
        };

        var result = _sut.CheckForCanaryFiles(files.Select(f => f.Path));
        result.Should().BeTrue();
    }

    [Fact]
    public void RecordAnomalousFile_RespectsThresholdAndTrips()
    {
        var backupId = Guid.NewGuid();

        // The bucket max is 10 per minute
        for (int i = 0; i < 9; i++)
        {
            _sut.RecordAnomalousFile(backupId);
            _sut.IsVelocityThresholdExceeded(backupId).Should().BeFalse();
        }

        // 10th strike trips the threshold
        _sut.RecordAnomalousFile(backupId);
        _sut.IsVelocityThresholdExceeded(backupId).Should().BeTrue();
    }
}
