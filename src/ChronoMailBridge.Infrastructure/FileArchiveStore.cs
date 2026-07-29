using System.Buffers;
using System.Security.Cryptography;
using ChronoMailBridge.Core;

namespace ChronoMailBridge.Infrastructure;

public sealed class FileArchiveStore : IArchiveStore
{
    private const long MinimumMarginBytes = 2L * 1024 * 1024 * 1024;

    public FileArchiveStore(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        RootPath = Path.GetFullPath(rootPath);
    }

    public string RootPath { get; }

    public Task EnsureLayoutAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(SafeNames.ResolveUnderRoot(RootPath, "messages"));
        Directory.CreateDirectory(SafeNames.ResolveUnderRoot(RootPath, "state"));
        Directory.CreateDirectory(SafeNames.ResolveUnderRoot(RootPath, "logs"));
        Directory.CreateDirectory(SafeNames.ResolveUnderRoot(RootPath, "reports"));
        return Task.CompletedTask;
    }

    public string GetMessagePath(SourceMessage message) =>
        SafeNames.ResolveUnderRoot(
            RootPath,
            "messages",
            SafeNames.FolderSegment(message.FolderName),
            message.UidValidity.ToString(System.Globalization.CultureInfo.InvariantCulture),
            $"{message.Uid}.eml");

    public async Task<ArchiveWriteResult> WriteAtomicAsync(
        SourceMessage message,
        Stream source,
        CancellationToken cancellationToken)
    {
        string finalPath = GetMessagePath(message);
        string directory = Path.GetDirectoryName(finalPath)
            ?? throw new InvalidOperationException("The archive path does not contain a directory.");
        Directory.CreateDirectory(directory);
        string partPath = finalPath + ".part";

        if (File.Exists(finalPath))
        {
            (string existingHash, long existingBytes) = await HashFileAsync(finalPath, cancellationToken)
                .ConfigureAwait(false);
            if (existingBytes == message.Size || message.Size <= 0)
            {
                return BuildResult(finalPath, existingHash, existingBytes, true);
            }

            string invalidPath = finalPath + $".invalid-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}";
            File.Move(finalPath, invalidPath);
        }

        if (File.Exists(partPath))
        {
            File.Delete(partPath);
        }

        byte[] buffer = ArrayPool<byte>.Shared.Rent(1024 * 1024);
        long bytes = 0;
        try
        {
            using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var destination = new FileStream(
                partPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 1024 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                while (true)
                {
                    int read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    hasher.AppendData(buffer, 0, read);
                    bytes += read;
                }

                await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
                destination.Flush(flushToDisk: true);
            }

            if (message.Size > 0 && bytes != message.Size)
            {
                throw new InvalidDataException(
                    $"Expected IMAP size {message.Size}; received size {bytes}.");
            }

            string sha256 = Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
            File.Move(partPath, finalPath);
            return BuildResult(finalPath, sha256, bytes, false);
        }
        catch
        {
            // Keep the .part file for diagnosis/reconciliation; the next attempt replaces it.
            throw;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public async Task<bool> VerifyAsync(
        string fullPath,
        string expectedSha256,
        long expectedSize,
        CancellationToken cancellationToken)
    {
        string safePath = EnsureUnderRoot(fullPath);
        if (!File.Exists(safePath))
        {
            return false;
        }

        (string sha256, long bytes) = await HashFileAsync(safePath, cancellationToken).ConfigureAwait(false);
        return bytes == expectedSize &&
            string.Equals(sha256, expectedSha256, StringComparison.OrdinalIgnoreCase);
    }

    public Task ReconcileAsync(IMigrationStore store, Guid jobId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string messagesRoot = SafeNames.ResolveUnderRoot(RootPath, "messages");
        if (Directory.Exists(messagesRoot))
        {
            foreach (string part in Directory.EnumerateFiles(messagesRoot, "*.part", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();
                // A partial file is never a safe checkpoint: it remains available for inspection and is replaced on retry.
                File.SetLastWriteTimeUtc(part, DateTime.UtcNow);
            }
        }

        return store.RequeueInterruptedAsync(jobId, cancellationToken);
    }

    public long GetAvailableBytes()
    {
        string? driveRoot = Path.GetPathRoot(RootPath);
        return driveRoot is null ? 0 : new DriveInfo(driveRoot).AvailableFreeSpace;
    }

    public bool HasRecommendedFreeSpace(long estimatedBytes)
    {
        long margin = Math.Max(MinimumMarginBytes, checked(estimatedBytes / 10));
        return GetAvailableBytes() >= checked(estimatedBytes + margin);
    }

    private static async Task<(string Sha256, long Bytes)> HashFileAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        string hash = await MessageIdentityRules.ComputeSha256Async(stream, cancellationToken).ConfigureAwait(false);
        return (hash, stream.Length);
    }

    private ArchiveWriteResult BuildResult(string fullPath, string hash, long bytes, bool reused) =>
        new(
            fullPath,
            Path.GetRelativePath(RootPath, fullPath),
            hash,
            bytes,
            reused);

    private string EnsureUnderRoot(string path)
    {
        string root = RootPath.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string fullPath = Path.GetFullPath(path);
        if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The path is outside the local archive.");
        }

        return fullPath;
    }
}
