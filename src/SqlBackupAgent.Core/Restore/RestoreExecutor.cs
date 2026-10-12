using System.Text;
using Microsoft.Extensions.Logging;
using SqlBackupAgent.Core.Catalog;
using SqlBackupAgent.Core.Jobs;
using SqlBackupAgent.Core.Sql;

namespace SqlBackupAgent.Core.Restore;

/// <summary>
/// Turns a <see cref="RestorePlan"/> into a RESTORE script and (optionally) runs it. Files no longer in the staging
/// folder are fetched back from a destination first. Restoring under a new name relocates every data/log file
/// to the instance's default folders, so it never collides with the source database.
/// </summary>
public sealed class RestoreExecutor(AgentContext ctx, ILogger<RestoreExecutor> logger)
{
    public async Task<string> BuildScriptAsync(SqlServerClient sql, RestorePlan plan, string targetDatabase, CancellationToken ct, bool fetchMissing = true)
    {
        var server = await sql.GetServerInfoAsync(ct);
        var files = new List<string>();
        foreach (var step in plan.Steps) files.Add(fetchMissing ? await EnsureStagedAsync(step.Backup, ct) : StagedOrPlaceholder(step.Backup));

        var renamed = !targetDatabase.Equals(plan.Database, StringComparison.OrdinalIgnoreCase);
        var moves = renamed && fetchMissing ? await MovesAsync(sql, server, files[0], targetDatabase, ct) : [];

        var sb = new StringBuilder()
            .AppendLine($"-- Restore {plan.Database} as {targetDatabase} to {(plan.PointInTime is null ? "the latest backup" : plan.RestoresTo.ToString("yyyy-MM-dd HH:mm:ss"))}")
            .AppendLine($"-- {plan.Steps.Count} backup(s): {string.Join(", ", plan.Steps.Select(s => s.Backup.Type))}");

        for (var i = 0; i < plan.Steps.Count; i++)
        {
            var step = plan.Steps[i];
            var with = new List<string> { "NORECOVERY", "CHECKSUM" };
            if (i == 0) with.Add("REPLACE");
            if (i == 0) with.AddRange(moves);
            if (step.StopAtApplies && plan.PointInTime is { } at) with.Add("STOPAT = " + TSql.StopAt(at));

            var verb = step.Backup.Type == BackupType.Log ? "LOG" : "DATABASE";
            sb.AppendLine($"RESTORE {verb} {TSql.QuoteName(targetDatabase)} FROM DISK = {TSql.Literal(files[i])} WITH {string.Join(", ", with)};");
        }

        return sb.AppendLine($"RESTORE DATABASE {TSql.QuoteName(targetDatabase)} WITH RECOVERY;").ToString();
    }

    public async Task RestoreAsync(SqlServerClient sql, RestorePlan plan, string targetDatabase, CancellationToken ct)
    {
        var script = await BuildScriptAsync(sql, plan, targetDatabase, ct);
        logger.LogInformation("Restoring {Source} as {Target} from {Count} backup(s)", plan.Database, targetDatabase, plan.Steps.Count);
        foreach (var statement in script.Split('\n').Where(l => l.StartsWith("RESTORE ", StringComparison.Ordinal)))
            await sql.ExecuteAsync(statement, 0, ct);
    }

    private async Task<IReadOnlyList<string>> MovesAsync(SqlServerClient sql, ServerInfo server, string fullBackup, string target, CancellationToken ct)
    {
        var separator = server.DefaultDataPath.Contains('/') ? "/" : "\\";
        var moves = new List<string>();
        var index = 0;
        foreach (var file in await sql.FileListAsync(fullBackup, ct))
        {
            var isLog = file.Type == "L";
            var folder = (isLog ? server.DefaultLogPath : server.DefaultDataPath).TrimEnd('/', '\\');
            var extension = isLog ? ".ldf" : index == 0 ? ".mdf" : ".ndf";
            var physical = $"{folder}{separator}{TSql.Safe(target)}_{TSql.Safe(file.LogicalName)}{extension}";
            moves.Add($"MOVE {TSql.Literal(file.LogicalName)} TO {TSql.Literal(physical)}");
            if (!isLog) index++;
        }
        return moves;
    }

    /// <summary>Returns the server-visible path of the backup, downloading it into staging if needed.</summary>
    private async Task<string> EnsureStagedAsync(BackupRecord backup, CancellationToken ct)
    {
        var staging = ctx.Options.Staging;
        if (backup.StagedServerPath is { } staged && File.Exists(staging.ToAgentPath(staged))) return staged;

        var serverFile = staging.ServerFile(backup.FileName);
        var agentFile = staging.ToAgentPath(serverFile);
        if (File.Exists(agentFile)) return serverFile;

        foreach (var location in backup.Locations)
        {
            if (ctx.DestinationFor(location) is not { } destination) continue;
            logger.LogInformation("Fetching {File} from {Destination}", backup.FileName, destination.Name);
            await destination.DownloadAsync(location, agentFile, ct);
            return serverFile;
        }
        throw new RestorePlanningException($"{backup.FileName} is not in staging and no destination holds a copy.");
    }

    private string StagedOrPlaceholder(BackupRecord backup) =>
        backup.StagedServerPath ?? ctx.Options.Staging.ServerFile(backup.FileName);
}
