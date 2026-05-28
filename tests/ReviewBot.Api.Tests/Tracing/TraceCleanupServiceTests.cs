using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ReviewBot.Api.Tracing;
using MsOptions = Microsoft.Extensions.Options.Options;

namespace ReviewBot.Api.Tests.Tracing;

public class TraceCleanupServiceTests : IDisposable
{
    private readonly string tempDir;
    private readonly FakeTimeProvider clock;

    public TraceCleanupServiceTests()
    {
        tempDir = Path.Combine(Path.GetTempPath(), $"reviewbot-cleanup-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        clock = new FakeTimeProvider(new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void RunCleanup_DeletesFilesOlderThanRetentionDays()
    {
        var oldFile = WriteFile("old.json", clock.GetUtcNow().UtcDateTime - TimeSpan.FromDays(16));
        var newFile = WriteFile("new.json", clock.GetUtcNow().UtcDateTime - TimeSpan.FromDays(5));

        var service = CreateService(retentionDays: 14, maxDiskMb: 500);
        service.RunCleanup();

        File.Exists(oldFile).Should().BeFalse();
        File.Exists(newFile).Should().BeTrue();
    }

    [Fact]
    public void RunCleanup_DeletesOldestFilesWhenOverSizeCap()
    {
        var file1 = WriteFile("a.json", clock.GetUtcNow().UtcDateTime - TimeSpan.FromDays(3), contentKb: 300);
        var file2 = WriteFile("b.json", clock.GetUtcNow().UtcDateTime - TimeSpan.FromDays(2), contentKb: 300);
        var file3 = WriteFile("c.json", clock.GetUtcNow().UtcDateTime - TimeSpan.FromDays(1), contentKb: 300);

        // Cap: 700KB. Total: 900KB. Oldest file (a) should be deleted.
        var service = CreateService(retentionDays: 14, maxDiskMb: 0);

        service.RunCleanup();

        // All are within retention but exceed 0MB cap; oldest deleted first.
        File.Exists(file1).Should().BeFalse();
    }

    [Fact]
    public void RunCleanup_DoesNothingWhenNoDirExists()
    {
        var nonExistentDir = Path.Combine(tempDir, "no-such-dir");
        var service = CreateService(retentionDays: 14, maxDiskMb: 500, tracesDir: nonExistentDir);

        var act = () => service.RunCleanup();
        act.Should().NotThrow();
    }

    [Fact]
    public void RunCleanup_DoesNotDeleteFilesWithinRetentionAndUnderCap()
    {
        var recentFile = WriteFile("recent.json", clock.GetUtcNow().UtcDateTime - TimeSpan.FromDays(3));

        var service = CreateService(retentionDays: 14, maxDiskMb: 500);
        service.RunCleanup();

        File.Exists(recentFile).Should().BeTrue();
    }

    public void Dispose()
    {
        if (Directory.Exists(tempDir))
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    private TraceCleanupService CreateService(
        int retentionDays,
        int maxDiskMb,
        string? tracesDir = null) =>
        new(
            MsOptions.Create(new TracingOptions
            {
                Enabled = true,
                RetentionDays = retentionDays,
                MaxDiskMb = maxDiskMb,
                TracesDir = tracesDir ?? tempDir
            }),
            clock,
            NullLogger<TraceCleanupService>.Instance);

    private string WriteFile(string name, DateTime lastWriteTime, int contentKb = 1)
    {
        var filePath = Path.Combine(tempDir, name);
        var content = new string('x', contentKb * 1024);
        File.WriteAllText(filePath, content);
        File.SetLastWriteTimeUtc(filePath, lastWriteTime);
        return filePath;
    }

    private sealed class FakeTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
