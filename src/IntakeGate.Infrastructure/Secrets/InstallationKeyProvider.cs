using System.Security.Cryptography;

namespace IntakeGate.Infrastructure.Secrets;

public sealed class InstallationKeyProvider
{
    public const int KeyLength = 32;
    private readonly string keyPath;
    private readonly SemaphoreSlim creationGate = new(1, 1);

    public InstallationKeyProvider(string keyPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyPath);
        this.keyPath = Path.GetFullPath(keyPath);
    }

    public string KeyPath => keyPath;

    public async Task<byte[]?> ValidateExistingAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(keyPath)) return null;
        return await ReadKeyAsync(cancellationToken);
    }

    public async Task<byte[]> GetOrCreateAsync(
        bool encryptedCredentialsExist,
        CancellationToken cancellationToken = default)
    {
        await creationGate.WaitAsync(cancellationToken);
        try
        {
            if (File.Exists(keyPath)) return await ReadKeyAsync(cancellationToken);
            if (encryptedCredentialsExist)
                throw new InstallationKeyException("EncryptionKeyMissing");

            var directory = Path.GetDirectoryName(keyPath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            var candidate = RandomNumberGenerator.GetBytes(KeyLength);
            try
            {
                try
                {
                    await using var stream = new FileStream(
                        keyPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096,
                        FileOptions.Asynchronous | FileOptions.WriteThrough);
                    await stream.WriteAsync(candidate, cancellationToken);
                    await stream.FlushAsync(cancellationToken);
                    RestrictPermissions();
                    return candidate;
                }
                catch (IOException) when (File.Exists(keyPath))
                {
                    CryptographicOperations.ZeroMemory(candidate);
                    return await ReadKeyAsync(cancellationToken);
                }
            }
            catch
            {
                CryptographicOperations.ZeroMemory(candidate);
                throw;
            }
        }
        finally
        {
            creationGate.Release();
        }
    }

    private async Task<byte[]> ReadKeyAsync(CancellationToken cancellationToken)
    {
        byte[] bytes;
        try
        {
            bytes = await File.ReadAllBytesAsync(keyPath, cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new InstallationKeyException("EncryptionKeyUnreadable");
        }

        if (bytes.Length != KeyLength)
        {
            CryptographicOperations.ZeroMemory(bytes);
            throw new InstallationKeyException("EncryptionKeyMalformed");
        }

        RestrictPermissions();
        return bytes;
    }

    private void RestrictPermissions()
    {
        if (OperatingSystem.IsWindows()) return;
        try
        {
            File.SetUnixFileMode(keyPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            throw new InstallationKeyException("EncryptionKeyPermissionsUnavailable");
        }
    }
}

public sealed class InstallationKeyException(string safeCategory)
    : InvalidOperationException($"Installation encryption material is unusable: {safeCategory}.")
{
    public string SafeCategory { get; } = safeCategory;
}
