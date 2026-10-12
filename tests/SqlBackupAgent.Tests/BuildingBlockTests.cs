using Microsoft.Extensions.Configuration;
using SqlBackupAgent.Core;
using SqlBackupAgent.Core.Catalog;
using SqlBackupAgent.Core.Destinations;
using SqlBackupAgent.Core.Sql;
using Shouldly;

namespace SqlBackupAgent.Tests;

public class TSqlTests
{
    [Fact]
    public void Names_and_literals_are_escaped_like_quotename()
    {
        TSql.QuoteName("Sales]; DROP DATABASE x; --").ShouldBe("[Sales]]; DROP DATABASE x; --]");
        TSql.Literal(@"C:\it's\file.bak").ShouldBe(@"N'C:\it''s\file.bak'");
    }

    [Fact]
    public void Full_backup_statement_has_checksum_compression_and_init()
    {
        TSql.Backup("Sales", BackupType.Full, @"D:\b\s.bak", compress: true, copyOnly: false).ShouldBe(
            @"BACKUP DATABASE [Sales] TO DISK = N'D:\b\s.bak' WITH INIT, FORMAT, SKIP, CHECKSUM, COMPRESSION, NAME = N'SqlBackupAgent Full Sales';");
    }

    [Fact]
    public void Log_and_differential_statements()
    {
        TSql.Backup("Sales", BackupType.Log, "/b/s.trn", false, false).ShouldStartWith("BACKUP LOG [Sales] TO DISK = N'/b/s.trn'");
        TSql.Backup("Sales", BackupType.Differential, "/b/s.bak", false, true).ShouldContain("DIFFERENTIAL, CHECKSUM, COPY_ONLY");
    }

    [Fact]
    public void File_names_and_keys_are_filesystem_safe()
    {
        var name = TSql.BackupFileName(@"SRV\SQL2022", "My DB", BackupType.Log, new DateTime(2026, 10, 12, 14, 5, 9));
        name.ShouldBe("SRV_SQL2022_My_DB_LOG_20261012_140509.trn");
        TSql.StorageKey(@"SRV\SQL2022", "My DB", BackupType.Log, name).ShouldBe("SRV_SQL2022/My_DB/log/" + name);
    }
}

public class DatabaseSelectorTests
{
    private static readonly DatabaseInfo[] All =
    [
        new("master", 1, "ONLINE", "SIMPLE", false), new("tempdb", 2, "ONLINE", "SIMPLE", false),
        new("model", 3, "ONLINE", "FULL", false), new("msdb", 4, "ONLINE", "SIMPLE", false),
        new("Sales", 5, "ONLINE", "FULL", false), new("SalesArchive", 6, "ONLINE", "FULL", true),
        new("HR", 7, "ONLINE", "SIMPLE", false), new("Old", 8, "OFFLINE", "FULL", false), new("Sales_drill_1", 9, "ONLINE", "FULL", false)
    ];

    private static string[] Names(IEnumerable<DatabaseInfo> dbs) => dbs.Select(d => d.Name).ToArray();

    [Fact]
    public void Star_selects_online_user_databases_only() =>
        Names(DatabaseSelector.Select(All, ["*"], ["*_drill_*"], false)).ShouldBe(["HR", "Sales", "SalesArchive"]);

    [Fact]
    public void Wildcards_and_excludes_work_together() =>
        Names(DatabaseSelector.Select(All, ["Sales*"], ["*Archive"], false)).ShouldBe(["Sales", "Sales_drill_1"]);

    [Fact]
    public void System_databases_need_opt_in_or_an_explicit_name()
    {
        Names(DatabaseSelector.Select(All, ["*"], [], true)).ShouldContain("master");
        Names(DatabaseSelector.Select(All, ["master", "msdb"], [], false)).ShouldBe(["master", "msdb"]);
    }

    [Fact]
    public void Tempdb_is_never_selected() => Names(DatabaseSelector.Select(All, ["tempdb", "*"], [], true)).ShouldNotContain("tempdb");
}

public class StagingTests
{
    [Fact]
    public void Server_paths_map_to_agent_paths_for_containers()
    {
        var staging = new StagingOptions { ServerPath = "/var/backups", AgentPath = Path.Combine("srv", "backups") };

        staging.ServerFile("a.bak").ShouldBe("/var/backups/a.bak");
        staging.ToAgentPath("/var/backups/a.bak").ShouldBe(Path.Combine("srv", "backups", "a.bak"));
    }

    [Fact]
    public void Without_agent_path_both_sides_are_the_same()
    {
        var staging = new StagingOptions { ServerPath = @"C:\staging" };
        staging.ToAgentPath(staging.ServerFile("a.bak")).ShouldBe(Path.Combine(@"C:\staging", "a.bak"));
    }
}

public class ScheduleTests
{
    [Fact]
    public void Next_occurrence_respects_cron_and_time_zone()
    {
        var job = new JobOptions { Schedule = "30 2 * * *", TimeZone = "UTC" };

        Schedule.Next(job, new DateTimeOffset(2026, 10, 12, 3, 0, 0, TimeSpan.Zero))
            .ShouldBe(new DateTimeOffset(2026, 10, 13, 2, 30, 0, TimeSpan.Zero));
    }
}

public class CatalogAndDestinationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"sba-{Guid.NewGuid():N}");

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    [Fact]
    public void Catalog_round_trips_lsns_beyond_double_precision()
    {
        var catalog = new BackupCatalog(_dir);
        var lsn = 98765432109876543210123m; // numeric(25,0)
        var id = catalog.Add(new BackupRecord
        {
            Job = "j", Server = "S", Database = "D", Type = BackupType.Log, FileName = "f.trn", Started = DateTime.Now, Finished = DateTime.Now,
            FirstLsn = lsn, LastLsn = lsn + 1, CheckpointLsn = 0, DatabaseBackupLsn = lsn - 5, Locations = ["local:x"]
        });

        var back = catalog.Backups(database: "d").Single();
        back.Id.ShouldBe(id);
        back.FirstLsn.ShouldBe(lsn);
        back.LastLsn.ShouldBe(lsn + 1);
        back.Locations.ShouldBe(["local:x"]);

        catalog.MarkDeleted(id, DateTime.UtcNow);
        catalog.Backups(database: "D").ShouldBeEmpty();
        catalog.Backups(database: "D", includeDeleted: true).Count.ShouldBe(1);
    }

    [Fact]
    public async Task Folder_destination_verifies_copies_and_refuses_path_escape()
    {
        var source = Path.Combine(_dir, "src.bak");
        Directory.CreateDirectory(_dir);
        await File.WriteAllTextAsync(source, "backup bytes");
        var sha = await FileHash.Sha256Async(source, CancellationToken.None);
        var destination = new FolderDestination("local", Path.Combine(_dir, "archive"));

        var location = await destination.UploadAsync(source, "SRV/Sales/full/src.bak", sha, CancellationToken.None);
        File.Exists(location["local:".Length..]).ShouldBeTrue();

        await Should.ThrowAsync<IntegrityException>(() => destination.UploadAsync(source, "SRV/x.bak", "deadbeef", CancellationToken.None));
        await Should.ThrowAsync<InvalidOperationException>(() => destination.UploadAsync(source, "../../escape.bak", sha, CancellationToken.None));

        var restored = Path.Combine(_dir, "restored.bak");
        await destination.DownloadAsync(location, restored, CancellationToken.None);
        (await File.ReadAllTextAsync(restored)).ShouldBe("backup bytes");
    }
}

public class ConfigurationBindingTests
{
    private static AgentOptions Bind(Dictionary<string, string?> values) =>
        new Microsoft.Extensions.Configuration.ConfigurationBuilder().AddInMemoryCollection(values).Build()
            .GetSection(AgentOptions.Section).Get<AgentOptions>()!;

    [Fact]
    public void Configured_database_list_is_used_exactly_as_written()
    {
        // Regression: a code default of ["*"] used to be merged with the configured list, selecting every database.
        var options = Bind(new() { ["Agent:Jobs:0:Name"] = "j", ["Agent:Jobs:0:Databases:0"] = "SbaDemo", ["Agent:Jobs:0:Destinations:0"] = "local" });

        options.Jobs.Single().Databases.ShouldBe(["SbaDemo"]);
        options.Jobs.Single().Destinations.ShouldBe(["local"]);
        options.Jobs.Single().Exclude.ShouldBeEmpty();
    }

    [Fact]
    public void A_job_without_databases_is_a_configuration_error_not_all_databases()
    {
        var options = Bind(new() { ["Agent:Staging:ServerPath"] = "C:/s", ["Agent:Jobs:0:Name"] = "j", ["Agent:Jobs:0:Destinations:0"] = "local",
            ["Agent:Destinations:local:Type"] = "Folder", ["Agent:Destinations:local:Path"] = "C:/a" });
        var ctx = new SqlBackupAgent.Core.Jobs.AgentContext(options, _ => "Server=x", new BackupCatalog(Path.Combine(Path.GetTempPath(), $"sba-{Guid.NewGuid():N}")), TimeProvider.System);

        ctx.Validate().ShouldContain(e => e.Contains("Databases must list at least one"));
    }
}
