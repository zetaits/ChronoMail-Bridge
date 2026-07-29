using ChronoMailBridge.Core;
using ChronoMailBridge.Infrastructure;

namespace ChronoMailBridge.Tests;

public sealed class PauseAndReportsTests
{
    [Fact]
    public async Task PauseWaitsUntilResume()
    {
        var pause = new PauseController();
        pause.Pause();
        Task waiting = pause.WaitIfPausedAsync(CancellationToken.None);
        Assert.False(waiting.IsCompleted);
        pause.Resume();
        await waiting;
        Assert.True(waiting.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task PauseHonorsCancellation()
    {
        var pause = new PauseController();
        pause.Pause();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => pause.WaitIfPausedAsync(cancellation.Token));
    }

    [Fact]
    public async Task ReportsContainNoMessageContent()
    {
        using var directory = new TemporaryDirectory();
        const string sensitive = "SECRET-SUBJECT";
        var snapshot = new MigrationSnapshot(
            [new("Inbox", 2020, MigrationStatus.Imported, 1, 10)],
            [new(7, "Inbox", DateTimeOffset.UtcNow, 10, MigrationStatus.NeedsReview, "mime_rejected")]);
        IReadOnlyList<string> files = await new CsvReportWriter().WriteAsync(
            directory.Path,
            snapshot,
            CancellationToken.None);

        foreach (string file in files)
        {
            string text = await File.ReadAllTextAsync(file);
            Assert.DoesNotContain(sensitive, text, StringComparison.Ordinal);
            Assert.DoesNotContain("subject", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("sender", text, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Theory]
    [InlineData(typeof(TimeoutException), ErrorKind.Temporary)]
    [InlineData(typeof(InvalidDataException), ErrorKind.Permanent)]
    public void ErrorClassifierSeparatesTemporaryAndPermanent(Type type, ErrorKind expected)
    {
        var exception = (Exception)Activator.CreateInstance(type)!;
        Assert.Equal(expected, new DefaultErrorClassifier().Classify(exception).Kind);
    }
}
