using System.Text;
using ChronoMailBridge.Core;

namespace ChronoMailBridge.Tests;

public sealed class RulesTests
{
    [Theory]
    [InlineData(" <ABC@example.COM> ", "<abc@example.com>")]
    [InlineData("ABC@example.COM", "<abc@example.com>")]
    [InlineData(null, null)]
    [InlineData("   ", null)]
    public void MessageIdIsNormalized(string? input, string? expected) =>
        Assert.Equal(expected, MessageIdentityRules.NormalizeMessageId(input));

    [Fact]
    public async Task Sha256MatchesKnownValue()
    {
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes("abc"));
        string hash = await MessageIdentityRules.ComputeSha256Async(stream, CancellationToken.None);
        Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", hash);
    }

    [Fact]
    public void MaximumDateIsInclusive()
    {
        DateTimeOffset maximum = new(2020, 12, 31, 23, 59, 59, TimeSpan.Zero);
        Assert.True(DateFilter.IsIncluded(maximum, maximum));
        Assert.False(DateFilter.IsIncluded(maximum.AddTicks(1), maximum));
    }

    [Fact]
    public void LabelsPreserveHierarchyAndSafeFlags()
    {
        IReadOnlyCollection<string> labels = GmailLabelMapper.Map(
            [("Inbox", FolderKind.Inbox), ("Clients\\2020", FolderKind.Normal)],
            isUnread: true,
            isStarred: true,
            "Imported from Turbify");
        Assert.Contains("INBOX", labels);
        Assert.Contains("UNREAD", labels);
        Assert.Contains("STARRED", labels);
        Assert.Contains("Imported from Turbify/Clients/2020", labels);
        Assert.DoesNotContain("SENT", labels);
        Assert.DoesNotContain("DRAFT", labels);
    }

    [Fact]
    public void SafePathRejectsTraversal()
    {
        string root = Path.Combine(Path.GetTempPath(), "chronomail-root");
        Assert.Throws<InvalidOperationException>(
            () => SafeNames.ResolveUnderRoot(root, "..", "outside.eml"));
    }

    [Fact]
    public void FolderSlugIsStableAndDoesNotTraverse()
    {
        string first = SafeNames.FolderSegment("../../CON");
        string second = SafeNames.FolderSegment("../../CON");
        Assert.Equal(first, second);
        Assert.DoesNotContain("..", first);
        Assert.DoesNotContain('/', first);
        Assert.DoesNotContain('\\', first);
    }

    [Fact]
    public void FullJitterBackoffIsTruncated()
    {
        var backoff = new FullJitterBackoff(RetrySettings.Conservative, new FixedRandom(1));
        Assert.Equal(TimeSpan.FromSeconds(5), backoff.GetDelay(1));
        Assert.Equal(TimeSpan.FromSeconds(10), backoff.GetDelay(2));
        Assert.Equal(TimeSpan.FromMinutes(5), backoff.GetDelay(20));
    }

    [Fact]
    public void CircuitBreakerOpensAfterFiveFailures()
    {
        var breaker = new CircuitBreaker(RetrySettings.Conservative);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        for (int attempt = 0; attempt < 4; attempt++)
        {
            Assert.False(breaker.Failure(now));
        }

        Assert.True(breaker.Failure(now));
        Assert.True(breaker.IsOpen(now));
        breaker.Success();
        Assert.False(breaker.IsOpen(now));
    }
}
