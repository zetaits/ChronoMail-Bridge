using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ChronoMailBridge.Core;
using Google.Apis.Util.Store;

namespace ChronoMailBridge.Infrastructure;

public sealed class DpapiSecretStore : ISecretStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("ChronoMailBridge/v1");
    private readonly string _directory;

    public DpapiSecretStore(string directory)
    {
        _directory = Path.GetFullPath(directory);
        Directory.CreateDirectory(_directory);
    }

    public async Task SaveAsync(string key, string secret, CancellationToken cancellationToken)
    {
        string path = GetPath(key);
        string temporary = path + ".part";
        byte[] protectedBytes = ProtectBytes(Encoding.UTF8.GetBytes(secret));
        await File.WriteAllBytesAsync(temporary, protectedBytes, cancellationToken).ConfigureAwait(false);
        File.Move(temporary, path, overwrite: true);
    }

    public async Task<string?> GetAsync(string key, CancellationToken cancellationToken)
    {
        string path = GetPath(key);
        if (!File.Exists(path))
        {
            return null;
        }

        byte[] protectedBytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        return Encoding.UTF8.GetString(UnprotectBytes(protectedBytes));
    }

    public Task DeleteAsync(string key, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string path = GetPath(key);
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        return Task.CompletedTask;
    }

    public string Protect(string value) =>
        Convert.ToBase64String(ProtectBytes(Encoding.UTF8.GetBytes(value)));

    public string Unprotect(string protectedValue) =>
        Encoding.UTF8.GetString(UnprotectBytes(Convert.FromBase64String(protectedValue)));

    private string GetPath(string key)
    {
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant();
        string path = Path.GetFullPath(Path.Combine(_directory, $"{hash}.bin"));
        string root = _directory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Invalid secret path.");
        }

        return path;
    }

    private static byte[] ProtectBytes(byte[] value) =>
        ProtectedData.Protect(value, Entropy, DataProtectionScope.CurrentUser);

    private static byte[] UnprotectBytes(byte[] value) =>
        ProtectedData.Unprotect(value, Entropy, DataProtectionScope.CurrentUser);
}

public sealed class DpapiGoogleDataStore : IDataStore
{
    private readonly ISecretStore _secrets;
    private readonly string _prefix;

    public DpapiGoogleDataStore(ISecretStore secrets, string prefix = "google-token")
    {
        _secrets = secrets;
        _prefix = prefix;
    }

    public Task ClearAsync() =>
        throw new NotSupportedException("Global deletion is not allowed; revoke the specific token.");

    public Task DeleteAsync<T>(string key) =>
        _secrets.DeleteAsync(StorageKey<T>(key), CancellationToken.None);

    public async Task<T?> GetAsync<T>(string key)
    {
        string? json = await _secrets.GetAsync(StorageKey<T>(key), CancellationToken.None).ConfigureAwait(false);
        return json is null ? default : JsonSerializer.Deserialize<T>(json);
    }

    public Task StoreAsync<T>(string key, T value) =>
        _secrets.SaveAsync(
            StorageKey<T>(key),
            JsonSerializer.Serialize(value),
            CancellationToken.None);

    private string StorageKey<T>(string key) => $"{_prefix}:{typeof(T).FullName}:{key}";
}
