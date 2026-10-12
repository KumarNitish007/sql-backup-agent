using Azure.Storage.Blobs;
using SqlBackupAgent.Core.Destinations;
using Shouldly;
using Testcontainers.Azurite;

namespace SqlBackupAgent.Tests;

/// <summary>Runs against Azurite (the Azure Storage emulator) in a container; skipped when Docker isn't available (CI provides it).</summary>
public sealed class AzureBlobDestinationTests : IAsyncLifetime
{
    private AzuriteContainer? _azurite;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"sba-az-{Guid.NewGuid():N}");

    public async Task InitializeAsync()
    {
        if (Environment.GetEnvironmentVariable("CI") is null) return;
        _azurite = new AzuriteBuilder("mcr.microsoft.com/azure-storage/azurite:3.35.0").Build();
        await _azurite.StartAsync();
        Directory.CreateDirectory(_dir);
    }

    public async Task DisposeAsync()
    {
        if (_azurite is not null) await _azurite.DisposeAsync();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    [SkippableFact]
    public async Task Uploads_verifies_downloads_and_deletes_blobs()
    {
        Skip.If(_azurite is null, "Azure Blob tests run in CI (Docker + Azurite).");

        var container = new BlobContainerClient(_azurite.GetConnectionString(), "sql-backups");
        var destination = new AzureBlobDestination("azure", container, "prod/");
        var file = Path.Combine(_dir, "db.bak");
        await File.WriteAllBytesAsync(file, Enumerable.Range(0, 300_000).Select(i => (byte)(i % 251)).ToArray());
        var sha = await FileHash.Sha256Async(file, CancellationToken.None);

        var location = await destination.UploadAsync(file, "SRV/Sales/full/db.bak", sha, CancellationToken.None);
        location.ShouldBe("azure:prod/SRV/Sales/full/db.bak");

        var copy = Path.Combine(_dir, "copy.bak");
        await destination.DownloadAsync(location, copy, CancellationToken.None);
        (await FileHash.Sha256Async(copy, CancellationToken.None)).ShouldBe(sha);

        await destination.DeleteAsync(location, CancellationToken.None);
        (await container.GetBlobClient("prod/SRV/Sales/full/db.bak").ExistsAsync()).Value.ShouldBeFalse();
    }
}
