using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace ChronoMailBridge.Core;

public static partial class MessageIdentityRules
{
    public static string? NormalizeMessageId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string normalized = value.Trim();
        if (normalized.StartsWith('<') && normalized.EndsWith('>') && normalized.Length > 2)
        {
            normalized = normalized[1..^1];
        }

        normalized = Whitespace().Replace(normalized, string.Empty);
        return normalized.Length == 0 ? null : $"<{normalized.ToLowerInvariant()}>";
    }

    public static async Task<string> ComputeSha256Async(Stream stream, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var sha = SHA256.Create();
        byte[] hash = await sha.ComputeHashAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Whitespace();
}

public static partial class SafeNames
{
    private static readonly HashSet<string> ReservedWindowsNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    public static string FolderSegment(string folderName)
    {
        string input = string.IsNullOrWhiteSpace(folderName) ? "folder" : folderName.Trim();
        string slug = InvalidPathChars().Replace(input, "-");
        slug = RepeatedSeparators().Replace(slug, "-").Trim(' ', '.', '-');
        if (slug.Length > 60)
        {
            slug = slug[..60].TrimEnd();
        }

        if (slug.Length == 0 || ReservedWindowsNames.Contains(slug))
        {
            slug = $"folder-{slug.ToLowerInvariant()}";
        }

        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input))).ToLowerInvariant()[..12];
        return $"{slug}~{hash}";
    }

    public static string ResolveUnderRoot(string root, params string[] segments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string candidate = Path.GetFullPath(Path.Combine([fullRoot, .. segments]));
        if (!candidate.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The calculated path is outside the job root.");
        }

        return candidate;
    }

    [GeneratedRegex(@"[<>:""/\\|?*\x00-\x1F]+", RegexOptions.CultureInvariant)]
    private static partial Regex InvalidPathChars();

    [GeneratedRegex(@"[-\s]+", RegexOptions.CultureInvariant)]
    private static partial Regex RepeatedSeparators();
}

public static class GmailLabelMapper
{
    public static IReadOnlyCollection<string> Map(
        IEnumerable<(string FolderName, FolderKind Kind)> folders,
        bool isUnread,
        bool isStarred,
        string prefix)
    {
        string safePrefix = NormalizeLabelPart(prefix);
        var labels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach ((string folderName, FolderKind kind) in folders)
        {
            string archival = $"{safePrefix}/{NormalizeHierarchy(folderName)}";
            labels.Add(archival);
            if (kind == FolderKind.Inbox)
            {
                labels.Add("INBOX");
            }
        }

        if (isUnread)
        {
            labels.Add("UNREAD");
        }

        if (isStarred)
        {
            labels.Add("STARRED");
        }

        return labels.Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static string NormalizeHierarchy(string value)
    {
        string[] parts = value
            .Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return string.Join('/', parts.Select(NormalizeLabelPart));
    }

    private static string NormalizeLabelPart(string value)
    {
        string cleaned = value
            .Replace('\0', '-')
            .Replace('\r', '-')
            .Replace('\n', '-')
            .Trim(' ', '/');
        return string.IsNullOrEmpty(cleaned) ? "Unnamed" : cleaned;
    }
}

public static class DateFilter
{
    public static bool IsIncluded(DateTimeOffset internalDate, DateTimeOffset? maximumInclusive) =>
        maximumInclusive is null || internalDate <= maximumInclusive.Value;
}

public sealed class FullJitterBackoff
{
    private readonly RetrySettings _settings;
    private readonly IRandomSource _random;

    public FullJitterBackoff(RetrySettings settings, IRandomSource random)
    {
        _settings = settings;
        _random = random;
    }

    public TimeSpan GetDelay(int failureNumber, TimeSpan? retryAfter = null)
    {
        if (retryAfter is { } explicitDelay && explicitDelay > TimeSpan.Zero)
        {
            return explicitDelay > _settings.MaximumDelay ? _settings.MaximumDelay : explicitDelay;
        }

        int exponent = Math.Clamp(failureNumber - 1, 0, 30);
        double capMs = Math.Min(
            _settings.MaximumDelay.TotalMilliseconds,
            _settings.InitialDelay.TotalMilliseconds * Math.Pow(2, exponent));
        return TimeSpan.FromMilliseconds(Math.Max(1, capMs * _random.NextDouble()));
    }
}

public sealed class CircuitBreaker
{
    private readonly RetrySettings _settings;
    private int _consecutiveFailures;

    public CircuitBreaker(RetrySettings settings) => _settings = settings;

    public DateTimeOffset? OpenUntilUtc { get; private set; }
    public bool IsOpen(DateTimeOffset now) => OpenUntilUtc > now;

    public void Success()
    {
        _consecutiveFailures = 0;
        OpenUntilUtc = null;
    }

    public bool Failure(DateTimeOffset now)
    {
        _consecutiveFailures++;
        if (_consecutiveFailures < _settings.CircuitFailureThreshold)
        {
            return false;
        }

        OpenUntilUtc = now + _settings.CircuitOpenDuration;
        _consecutiveFailures = 0;
        return true;
    }
}

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

public sealed class TaskDelay : IDelay
{
    public Task WaitAsync(TimeSpan delay, CancellationToken cancellationToken) =>
        Task.Delay(delay, cancellationToken);
}

public sealed class ThreadSafeRandomSource : IRandomSource
{
    public double NextDouble() => Random.Shared.NextDouble();
}
