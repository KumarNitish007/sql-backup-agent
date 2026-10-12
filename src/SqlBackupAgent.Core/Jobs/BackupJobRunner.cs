using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using SqlBackupAgent.Core.Catalog;
using SqlBackupAgent.Core.Destinations;
using SqlBackupAgent.Core.Retention;
using SqlBackupAgent.Core.Sql;

namespace SqlBackupAgent.Core.Jobs;

/// <summary>
/// Runs one backup job: for each selected database, back up to the staging folder, verify, record LSNs,
/// copy to every destination (with checksum verification), then apply retention. One failing database
/// never stops the others.
/// </summary>
public sealed class BackupJobRunner(AgentContext ctx, ILogger<BackupJobRunner> logger)
{
    public async Task<IReadOnlyList<RunRecord>> RunAsync(JobOptions job, CancellationToken ct)
    {
        var sql = ctx.Sql(job.Connection);
        var server = await sql.GetServerInfoAsync(ct);
        var databases = DatabaseSelector.Select(await sql.ListDatabasesAsync(ct), job.Databases, job.Exclude, job.IncludeSystemDatabases);
        if (databases.Count == 0) logger.LogWarning("Job {Job} matched no online databases", job.Name);

        var compress = job.Compress && server.SupportsCompression;
        if (job.Compress && !server.SupportsCompression)
            logger.LogInformation("{Server} (Express/LocalDB) does not support backup compression; backing up uncompressed", server.ServerName);

        var runs = new List<RunRecord>();
        foreach (var db in databases)
            runs.Add(await BackupDatabaseAsync(job, sql, server, db, compress, ct));

        await ApplyRetentionAsync(server.ServerName, databases.Select(d => d.Name), ct);
        return runs;
    }

    private async Task<RunRecord> BackupDatabaseAsync(JobOptions job, SqlServerClient sql, ServerInfo server, DatabaseInfo db, bool compress, CancellationToken ct)
    {
        var started = ctx.Clock.GetUtcNow().UtcDateTime;
        RunRecord Run(RunStatus status, string? message, long? backupId = null) => new()
        {
            Job = job.Name, Kind = "Backup", Database = db.Name, Status = status, StartedUtc = started,
            FinishedUtc = ctx.Clock.GetUtcNow().UtcDateTime, Message = message, BackupId = backupId
        };

        var type = job.Type;
        if (type == BackupType.Log && db.RecoveryModel.Equals("SIMPLE", StringComparison.OrdinalIgnoreCase))
            return Save(Run(RunStatus.Skipped, "SIMPLE recovery model - log backups are not possible."));
        if (type != BackupType.Full && db.Name.Equals("master", StringComparison.OrdinalIgnoreCase))
            return Save(Run(RunStatus.Skipped, "master supports full backups only."));

        string? note = null;
        try
        {
            string serverFile;
            try
            {
                serverFile = await BackupAsync(sql, server, db.Name, type, compress, job, ct);
            }
            catch (SqlException ex) when (type != BackupType.Full && ex.Number is SqlServerClient.NoCurrentFullBackup or SqlServerClient.NoLogBackupPossible)
            {
                // No base full yet (new database, or chain broken by a SIMPLE→FULL switch): take a full instead.
                note = $"No current full backup; took a full instead of {type}.";
                logger.LogWarning("{Database}: {Note}", db.Name, note);
                type = BackupType.Full;
                serverFile = await BackupAsync(sql, server, db.Name, type, compress, job, ct);
            }

            var set = await sql.GetBackupSetAsync(serverFile, ct);
            var agentFile = ctx.Options.Staging.ToAgentPath(serverFile);
            var sha = await FileHash.Sha256Async(agentFile, ct);
            var fileName = Path.GetFileName(agentFile);
            var key = TSql.StorageKey(server.ServerName, db.Name, type, fileName);

            var locations = new List<string>();
            var uploadErrors = new List<string>();
            foreach (var name in job.Destinations)
            {
                try
                {
                    locations.Add(await ctx.Destinations[name].UploadAsync(agentFile, key, sha, ct));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Copying {File} to {Destination} failed", fileName, name);
                    uploadErrors.Add($"{name}: {ex.Message}");
                }
            }

            var allCopied = uploadErrors.Count == 0 && locations.Count > 0;
            var deleteStaged = allCopied && ctx.Options.Staging.DeleteAfterUpload;
            var id = ctx.Catalog.Add(new BackupRecord
            {
                Job = job.Name, Server = server.ServerName, Database = db.Name, Type = type, FileName = fileName,
                Started = set.Started, Finished = set.Finished, SizeBytes = new FileInfo(agentFile).Length, Sha256 = sha,
                FirstLsn = set.FirstLsn, LastLsn = set.LastLsn, CheckpointLsn = set.CheckpointLsn, DatabaseBackupLsn = set.DatabaseBackupLsn,
                CopyOnly = job.CopyOnly, Verified = job.Verify, Locations = locations,
                StagedServerPath = deleteStaged ? null : serverFile, Note = note
            });
            if (deleteStaged) File.Delete(agentFile);

            logger.LogInformation("Backed up {Database} ({Type}, {Size:N0} bytes) to {Count} destination(s)", db.Name, type, set.SizeBytes, locations.Count);
            return Save(uploadErrors.Count == 0
                ? Run(note is null ? RunStatus.Succeeded : RunStatus.Warning, note, id)
                : Run(locations.Count > 0 ? RunStatus.Warning : RunStatus.Failed, "Copy failed: " + string.Join("; ", uploadErrors), id));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Backup of {Database} failed", db.Name);
            return Save(Run(RunStatus.Failed, ex.Message));
        }
    }

    private async Task<string> BackupAsync(SqlServerClient sql, ServerInfo server, string database, BackupType type, bool compress, JobOptions job, CancellationToken ct)
    {
        var stamp = ctx.Clock.GetLocalNow().DateTime;
        var serverFile = ctx.Options.Staging.ServerFile(TSql.BackupFileName(server.ServerName, database, type, stamp));
        await sql.ExecuteAsync(TSql.Backup(database, type, serverFile, compress, job.CopyOnly), job.CommandTimeoutSeconds, ct);
        if (job.Verify) await sql.ExecuteAsync(TSql.VerifyOnly(serverFile), job.CommandTimeoutSeconds, ct);
        return serverFile;
    }

    private async Task ApplyRetentionAsync(string server, IEnumerable<string> databases, CancellationToken ct)
    {
        var now = ctx.Clock.GetLocalNow().DateTime;
        foreach (var database in databases)
        {
            var history = ctx.Catalog.Backups(server, database);
            foreach (var backup in RetentionPolicy.SelectForDeletion(history, ctx.Options.Retention, now))
            {
                try
                {
                    foreach (var location in backup.Locations)
                        if (ctx.DestinationFor(location) is { } destination) await destination.DeleteAsync(location, ct);
                    if (backup.StagedServerPath is not null) File.Delete(ctx.Options.Staging.ToAgentPath(backup.StagedServerPath));
                    ctx.Catalog.MarkDeleted(backup.Id, DateTime.UtcNow);
                    logger.LogInformation("Retention removed {File}", backup.FileName);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Retention could not remove {File}; will retry next run", backup.FileName);
                }
            }
        }
    }

    private RunRecord Save(RunRecord run) => run with { Id = ctx.Catalog.AddRun(run) };
}
