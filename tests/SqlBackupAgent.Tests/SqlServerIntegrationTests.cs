using DotNet.Testcontainers.Builders;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using SqlBackupAgent.Core;
using SqlBackupAgent.Core.Catalog;
using SqlBackupAgent.Core.Jobs;
using SqlBackupAgent.Core.Restore;
using Shouldly;
using Testcontainers.MsSql;

namespace SqlBackupAgent.Tests;

/// <summary>
/// A real SQL Server for integration tests:
/// <list type="bullet">
/// <item>SQLBACKUP_TEST_SQL set (e.g. LocalDB): use it; SQL Server and the agent share the machine, so paths are identical.</item>
/// <item>Otherwise, with Docker (CI): a SQL Server 2022 container whose /backups is bind-mounted to a host folder,
/// exercising the ServerPath/AgentPath mapping exactly like a containerised production setup.</item>
/// <item>Neither: the tests are skipped.</item>
/// </list>
/// </summary>
public sealed class SqlServerFixture : IAsyncLifetime
{
    private MsSqlContainer? _container;

    public string? ConnectionString { get; private set; }
    public string Root { get; } = Path.Combine(Path.GetTempPath(), $"sba-it-{Guid.NewGuid():N}");
    public StagingOptions Staging { get; private set; } = new();
    public string? SkipReason { get; private set; }

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(Root);
        var external = Environment.GetEnvironmentVariable("SQLBACKUP_TEST_SQL");
        if (!string.IsNullOrWhiteSpace(external))
        {
            ConnectionString = external;
            Staging = new StagingOptions { ServerPath = Path.Combine(Root, "staging"), DeleteAfterUpload = false };
            Directory.CreateDirectory(Staging.ServerPath);
            return;
        }

        if (Environment.GetEnvironmentVariable("CI") is null)
        {
            SkipReason = "Set SQLBACKUP_TEST_SQL (e.g. LocalDB) or run in CI with Docker to execute SQL Server integration tests.";
            return;
        }

        var hostStaging = Path.Combine(Root, "staging");
        Directory.CreateDirectory(hostStaging);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(hostStaging, (UnixFileMode)0b111_111_111); // the mssql user (uid 10001) must be able to write

        _container = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest")
            .WithBindMount(hostStaging, "/backups")
            .Build();
        await _container.StartAsync();
        ConnectionString = _container.GetConnectionString();
        Staging = new StagingOptions { ServerPath = "/backups", AgentPath = hostStaging, DeleteAfterUpload = false };
    }

    public async Task DisposeAsync()
    {
        if (_container is not null) await _container.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(Root, true); } catch (Exception) { /* SQL Server may still hold a file briefly */ }
    }
}

public sealed class SqlServerIntegrationTests(SqlServerFixture fx) : IClassFixture<SqlServerFixture>, IAsyncLifetime
{
    private readonly string _db = $"SbaTest_{Guid.NewGuid():N}"[..20];
    private readonly List<string> _createdDatabases = [];
    private AgentContext _ctx = null!;

    public Task InitializeAsync()
    {
        if (fx.SkipReason is not null) return Task.CompletedTask;
        _createdDatabases.Add(_db);
        var options = new AgentOptions
        {
            StateDirectory = Path.Combine(fx.Root, $"state-{Guid.NewGuid():N}"),
            Staging = fx.Staging,
            Destinations = new(StringComparer.OrdinalIgnoreCase) { ["local"] = new DestinationOptions { Type = "Folder", Path = Path.Combine(fx.Root, "archive") } },
            Retention = new RetentionOptions { KeepDays = 30 }
        };
        _ctx = new AgentContext(options, _ => fx.ConnectionString, new BackupCatalog(options.StateDirectory), TimeProvider.System);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (fx.SkipReason is not null) return;
        foreach (var db in _createdDatabases)
            await ExecAsync($"IF DB_ID(N'{db}') IS NOT NULL BEGIN ALTER DATABASE [{db}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{db}]; END");
    }

    private JobOptions Job(BackupType type, string? database = null) => new()
    {
        Name = $"it-{type}", Type = type, Databases = [database ?? _db], Destinations = ["local"], Compress = true, Verify = true
    };

    private Task<IReadOnlyList<RunRecord>> RunAsync(JobOptions job) => new BackupJobRunner(_ctx, NullLogger<BackupJobRunner>.Instance).RunAsync(job, CancellationToken.None);

    [SkippableFact]
    public async Task Full_diff_and_log_backups_restore_to_an_exact_point_in_time()
    {
        Skip.If(fx.SkipReason is not null, fx.SkipReason);
        await CreateDatabaseAsync(_db, recovery: "FULL");
        await InsertAsync(_db, 100);

        await ShouldSucceedAsync(Job(BackupType.Full));
        await InsertAsync(_db, 50);
        await ShouldSucceedAsync(Job(BackupType.Differential));
        await InsertAsync(_db, 25);                                    // 175 rows
        await Task.Delay(1500);
        var pointInTime = await ScalarAsync<DateTime>("SELECT SYSDATETIME()");
        await Task.Delay(1500);
        await InsertAsync(_db, 10);                                    // 185 rows
        await ShouldSucceedAsync(Job(BackupType.Log));

        var history = _ctx.Catalog.Backups(database: _db);
        history.Select(b => b.Type).ShouldBe([BackupType.Log, BackupType.Differential, BackupType.Full]); // newest first, ties broken by insertion order
        history.ShouldAllBe(b => b.Locations.Count == 1 && b.Sha256 != null && b.LastLsn > 0);
        foreach (var b in history) File.Exists(b.Locations[0]["local:".Length..]).ShouldBeTrue();

        var executor = new RestoreExecutor(_ctx, NullLogger<RestoreExecutor>.Instance);
        var sql = _ctx.Sql("Default");

        var pitDb = _db + "_pit";
        _createdDatabases.Add(pitDb);
        var pitPlan = RestorePlanner.Plan(_db, history, pointInTime);
        pitPlan.Steps.Select(s => s.Backup.Type).ShouldBe([BackupType.Full, BackupType.Differential, BackupType.Log]);
        await executor.RestoreAsync(sql, pitPlan, pitDb, CancellationToken.None);
        (await ScalarAsync<int>("SELECT COUNT(*) FROM dbo.Orders", pitDb)).ShouldBe(175);

        var latestDb = _db + "_latest";
        _createdDatabases.Add(latestDb);
        await executor.RestoreAsync(sql, RestorePlanner.Plan(_db, history), latestDb, CancellationToken.None);
        (await ScalarAsync<int>("SELECT COUNT(*) FROM dbo.Orders", latestDb)).ShouldBe(185);
    }

    [SkippableFact]
    public async Task Differential_without_a_full_is_promoted_to_full()
    {
        Skip.If(fx.SkipReason is not null, fx.SkipReason);
        await CreateDatabaseAsync(_db, recovery: "FULL");

        var run = (await RunAsync(Job(BackupType.Differential))).Single();

        run.Status.ShouldBe(RunStatus.Warning, run.Message);
        run.Message!.ShouldContain("took a full instead");
        _ctx.Catalog.Backups(database: _db).Single().Type.ShouldBe(BackupType.Full);
    }

    [SkippableFact]
    public async Task Log_backups_skip_simple_recovery_databases()
    {
        Skip.If(fx.SkipReason is not null, fx.SkipReason);
        await CreateDatabaseAsync(_db, recovery: "SIMPLE");

        var run = (await RunAsync(Job(BackupType.Log))).Single();

        run.Status.ShouldBe(RunStatus.Skipped);
        _ctx.Catalog.Backups(database: _db).ShouldBeEmpty();
    }

    [SkippableFact]
    public async Task Restore_drill_restores_checks_and_cleans_up()
    {
        Skip.If(fx.SkipReason is not null, fx.SkipReason);
        await CreateDatabaseAsync(_db, recovery: "FULL");
        await InsertAsync(_db, 20);
        await ShouldSucceedAsync(Job(BackupType.Full));

        var drill = new RestoreDrillRunner(_ctx, new RestoreExecutor(_ctx, NullLogger<RestoreExecutor>.Instance), NullLogger<RestoreDrillRunner>.Instance);
        var run = (await drill.RunAsync(new JobOptions { Name = "drill", Kind = JobKind.RestoreDrill, Databases = [_db] }, CancellationToken.None)).Single();

        run.Status.ShouldBe(RunStatus.Succeeded, run.Message);
        run.Message!.ShouldContain("CHECKDB clean");
        (await ScalarAsync<int>($"SELECT COUNT(*) FROM sys.databases WHERE name LIKE N'{_db}_drill_%'")).ShouldBe(0);
    }

    // ---------------------------------------------------------------- helpers

    private async Task ShouldSucceedAsync(JobOptions job)
    {
        var run = (await RunAsync(job)).Single();
        run.Status.ShouldBe(RunStatus.Succeeded, $"{job.Type} backup failed: {run.Message}");
    }

    private async Task CreateDatabaseAsync(string name, string recovery)
    {
        await ExecAsync($"CREATE DATABASE [{name}]; ALTER DATABASE [{name}] SET RECOVERY {recovery};");
        await ExecAsync("CREATE TABLE dbo.Orders (Id int IDENTITY PRIMARY KEY, Amount decimal(10,2) NOT NULL, CreatedAt datetime2 NOT NULL DEFAULT SYSDATETIME());", name);
    }

    private Task InsertAsync(string database, int rows) =>
        ExecAsync($"INSERT dbo.Orders (Amount) SELECT TOP ({rows}) ABS(CHECKSUM(NEWID())) % 1000 FROM sys.all_objects;", database);

    private async Task ExecAsync(string sql, string database = "master")
    {
        await using var c = new SqlConnection(new SqlConnectionStringBuilder(fx.ConnectionString) { InitialCatalog = database }.ConnectionString);
        await c.OpenAsync();
        await using var cmd = new SqlCommand(sql, c) { CommandTimeout = 300 };
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<T> ScalarAsync<T>(string sql, string database = "master")
    {
        await using var c = new SqlConnection(new SqlConnectionStringBuilder(fx.ConnectionString) { InitialCatalog = database }.ConnectionString);
        await c.OpenAsync();
        await using var cmd = new SqlCommand(sql, c);
        return (T)(await cmd.ExecuteScalarAsync())!;
    }
}
