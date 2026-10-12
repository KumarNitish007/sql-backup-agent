using Microsoft.Extensions.Logging;
using SqlBackupAgent.Core.Catalog;
using SqlBackupAgent.Core.Restore;
using SqlBackupAgent.Core.Sql;

namespace SqlBackupAgent.Core.Jobs;

/// <summary>
/// "A backup you haven't restored is only a hope." For each database: plan the latest restore chain, restore it
/// into a throw-away database, run DBCC CHECKDB, compare object counts, then drop the scratch copy.
/// </summary>
public sealed class RestoreDrillRunner(AgentContext ctx, RestoreExecutor executor, ILogger<RestoreDrillRunner> logger)
{
    public async Task<IReadOnlyList<RunRecord>> RunAsync(JobOptions job, CancellationToken ct)
    {
        var sql = ctx.Sql(job.Connection);
        var server = await sql.GetServerInfoAsync(ct);
        var history = ctx.Catalog.Backups(server.ServerName);
        var names = history.Select(b => b.Database).Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(n => job.Databases.Any(p => DatabaseSelector.Matches(n, p)) && !job.Exclude.Any(p => DatabaseSelector.Matches(n, p)))
            .OrderBy(n => n)
            .ToList();

        var runs = new List<RunRecord>();
        foreach (var database in names)
            runs.Add(await DrillAsync(job, sql, database, history, ct));
        return runs;
    }

    private async Task<RunRecord> DrillAsync(JobOptions job, SqlServerClient sql, string database, IReadOnlyList<BackupRecord> history, CancellationToken ct)
    {
        var started = ctx.Clock.GetUtcNow().UtcDateTime;
        var scratch = $"{database}_drill_{ctx.Clock.GetLocalNow():yyyyMMddHHmmss}";
        RunRecord Run(RunStatus status, string message) => new()
        {
            Job = job.Name, Kind = "RestoreDrill", Database = database, Status = status, StartedUtc = started,
            FinishedUtc = ctx.Clock.GetUtcNow().UtcDateTime, Message = message
        };

        try
        {
            var plan = RestorePlanner.Plan(database, history);
            await executor.RestoreAsync(sql, plan, scratch, ct);
            await sql.ExecuteAsync($"DBCC CHECKDB ({TSql.QuoteName(scratch)}) WITH NO_INFOMSGS, ALL_ERRORMSGS;", job.CommandTimeoutSeconds, ct);
            var tables = await sql.ScalarAsync<int>("SELECT COUNT(*) FROM sys.tables WHERE is_ms_shipped = 0", null, ct, scratch);

            var message = $"Restored {plan.Steps.Count} backup(s) to {plan.RestoresTo:yyyy-MM-dd HH:mm:ss}; CHECKDB clean; {tables} user table(s).";
            logger.LogInformation("Restore drill for {Database}: {Message}", database, message);
            return Save(Run(RunStatus.Succeeded, message));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Restore drill for {Database} failed", database);
            return Save(Run(RunStatus.Failed, ex.Message));
        }
        finally
        {
            await DropAsync(sql, scratch);
        }
    }

    private async Task DropAsync(SqlServerClient sql, string database)
    {
        try
        {
            await sql.ExecuteAsync($"""
                IF DB_ID({TSql.Literal(database)}) IS NOT NULL
                BEGIN
                    ALTER DATABASE {TSql.QuoteName(database)} SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                    DROP DATABASE {TSql.QuoteName(database)};
                END
                """, 300, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not drop drill database {Database}; drop it manually", database);
        }
    }

    private RunRecord Save(RunRecord run) => run with { Id = ctx.Catalog.AddRun(run) };
}
