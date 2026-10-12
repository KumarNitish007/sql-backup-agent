using System.Security.Cryptography;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;

namespace SqlBackupAgent.Core.Destinations;

/// <summary>Somewhere backups are copied to. A location string identifies one stored copy, e.g. "local:D:\Backups\x.bak".</summary>
public interface IBackupDestination
{
    string Name { get; }

    /// <summary>Copies the file, verifies the stored copy's SHA-256, and returns its location string.</summary>
    Task<string> UploadAsync(string localFile, string key, string sha256, CancellationToken ct);

    Task DownloadAsync(string location, string localFile, CancellationToken ct);

    Task DeleteAsync(string location, CancellationToken ct);

    bool Owns(string location) => location.StartsWith(Name + ":", StringComparison.OrdinalIgnoreCase);
}

public static class FileHash
{
    public static async Task<string> Sha256Async(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)).ToLowerInvariant();
    }
}

public sealed class IntegrityException(string message) : Exception(message);

/// <summary>A local folder or network share.</summary>
public sealed class FolderDestination(string name, string root) : IBackupDestination
{
    public string Name => name;

    public async Task<string> UploadAsync(string localFile, string key, string sha256, CancellationToken ct)
    {
        var target = Path.GetFullPath(Path.Combine(root, key.Replace('/', Path.DirectorySeparatorChar)));
        if (!target.StartsWith(Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Key escapes the destination folder.");

        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var temp = target + ".partial";
        await using (var source = File.OpenRead(localFile))
        await using (var destination = File.Create(temp))
            await source.CopyToAsync(destination, ct);

        if (await FileHash.Sha256Async(temp, ct) != sha256)
        {
            File.Delete(temp);
            throw new IntegrityException($"Copy to {target} failed verification.");
        }
        File.Move(temp, target, overwrite: true); // readers never see half-written files
        return $"{Name}:{target}";
    }

    public async Task DownloadAsync(string location, string localFile, CancellationToken ct)
    {
        await using var source = File.OpenRead(PathOf(location));
        await using var destination = File.Create(localFile);
        await source.CopyToAsync(destination, ct);
    }

    public Task DeleteAsync(string location, CancellationToken ct)
    {
        File.Delete(PathOf(location));
        return Task.CompletedTask;
    }

    private string PathOf(string location) => location[(Name.Length + 1)..];
}

/// <summary>Azure Blob Storage. The SHA-256 is stored as blob metadata and re-checked after upload.</summary>
public sealed class AzureBlobDestination(string name, BlobContainerClient container, string? prefix) : IBackupDestination
{
    public string Name => name;

    public async Task<string> UploadAsync(string localFile, string key, string sha256, CancellationToken ct)
    {
        await container.CreateIfNotExistsAsync(cancellationToken: ct);
        var blobName = (prefix ?? "") + key;
        var blob = container.GetBlobClient(blobName);

        await blob.UploadAsync(localFile, new BlobUploadOptions
        {
            Metadata = new Dictionary<string, string> { ["sha256"] = sha256 },
            HttpHeaders = new BlobHttpHeaders { ContentType = "application/octet-stream" },
            TransferOptions = new Azure.Storage.StorageTransferOptions { MaximumConcurrency = 4 }
        }, ct);

        var properties = await blob.GetPropertiesAsync(cancellationToken: ct);
        if (properties.Value.ContentLength != new FileInfo(localFile).Length)
            throw new IntegrityException($"Uploaded blob {blobName} has the wrong size.");

        return $"{Name}:{blobName}";
    }

    public async Task DownloadAsync(string location, string localFile, CancellationToken ct)
    {
        var blob = container.GetBlobClient(location[(Name.Length + 1)..]);
        await blob.DownloadToAsync(localFile, ct);
        var metadata = (await blob.GetPropertiesAsync(cancellationToken: ct)).Value.Metadata;
        var expected = metadata.TryGetValue("sha256", out var hash) ? hash : null;
        if (expected is not null && await FileHash.Sha256Async(localFile, ct) != expected)
            throw new IntegrityException($"Downloaded {location} does not match its recorded SHA-256.");
    }

    public Task DeleteAsync(string location, CancellationToken ct) =>
        container.GetBlobClient(location[(Name.Length + 1)..]).DeleteIfExistsAsync(cancellationToken: ct);
}

public static class DestinationFactory
{
    public static IBackupDestination Create(string name, DestinationOptions o, Func<string, string?> connectionString) =>
        o.Type.ToLowerInvariant() switch
        {
            "folder" => new FolderDestination(name, o.Path ?? throw new InvalidOperationException($"Destination '{name}' needs a Path.")),
            "azureblob" => new AzureBlobDestination(name,
                new BlobContainerClient(
                    connectionString(o.ConnectionStringName ?? throw new InvalidOperationException($"Destination '{name}' needs ConnectionStringName."))
                    ?? throw new InvalidOperationException($"Connection string '{o.ConnectionStringName}' is not configured."),
                    o.Container ?? throw new InvalidOperationException($"Destination '{name}' needs a Container.")),
                o.Prefix),
            _ => throw new InvalidOperationException($"Unknown destination type '{o.Type}' for '{name}'.")
        };
}
