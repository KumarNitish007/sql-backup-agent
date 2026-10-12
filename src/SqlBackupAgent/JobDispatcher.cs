using System.Collections.Concurrent;
using Cronos;
using SqlBackupAgent.Core;
using SqlBackupAgent.Core.Catalog;
using SqlBackupAgent.Core.Jobs;
using SqlBackupAgent.Core.Notifications;

namespace SqlBackupAgent;

public sealed record JobState(string Name, string Kind, string Type, string Schedule, bool Enabled, bool Running, DateTimeOffset? NextRun, DateTimeOffset? LastRun, string? LastResult);

/// <summary>Runs jobs (scheduled or on demand), never two instances of the same job at once, at most N jobs in parallel.</summary>
public sealed class JobDispatcher(AgentContext ctx, BackupJobRunner backups, RestoreDrillRunner drills, Notifier notifier, ILogger<JobDispatcher> logger)
{
    private readonly SemaphoreSlim _slots = new(Math.Max(1, ctx.Options.MaxConcurrentJobs));
    private readonly ConcurrentDictionary<string, byte> _running = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, (DateTimeOffset At, string Result)> _last = new(StringComparer.OrdinalIgnoreCase);

    public JobOptions? Find(string name) => ctx.Options.Jobs.FirstOrDefault(j => j.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Returns false if the job is already running.</summary>
    public async Task<(bool Started, IReadOnlyList<RunRecord> Runs)> RunAsync(JobOptions job, CancellationToken ct)
    {
        if (!_running.TryAdd(job.Name, 0)) return (false, []);
        try
        {
            await _slots.WaitAsync(ct);
            try
            {
                logger.LogInformation("Job {Job} started", job.Name);
                var runs = job.Kind == JobKind.RestoreDrill ? await drills.RunAsync(job, ct) : await backups.RunAsync(job, ct);
                var report = new JobReport(job.Name, job.Kind.ToString(), runs);
                _last[job.Name] = (DateTimeOffset.Now, report.Title);
                await notifier.NotifyAsync(report, ct);
                logger.LogInformation("{Title}", report.Title);
                return (true, runs);
            }
            finally
            {
                _slots.Release();
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Connection failures etc. that prevent the job from even listing databases.
            logger.LogError(ex, "Job {Job} could not run", job.Name);
            var run = new RunRecord { Job = job.Name, Kind = job.Kind.ToString(), Database = "*", Status = RunStatus.Failed, StartedUtc = DateTime.UtcNow, FinishedUtc = DateTime.UtcNow, Message = ex.Message };
            ctx.Catalog.AddRun(run);
            var report = new JobReport(job.Name, job.Kind.ToString(), [run]);
            _last[job.Name] = (DateTimeOffset.Now, report.Title);
            await notifier.NotifyAsync(report, ct);
            return (true, [run]);
        }
        finally
        {
            _running.TryRemove(job.Name, out _);
        }
    }

    public IReadOnlyList<JobState> States() => ctx.Options.Jobs.Select(j =>
    {
        var last = _last.TryGetValue(j.Name, out var l) ? l : default;
        return new JobState(j.Name, j.Kind.ToString(), j.Kind == JobKind.Backup ? j.Type.ToString() : "-", j.Schedule, j.Enabled,
            _running.ContainsKey(j.Name), j.Enabled ? Schedule.Next(j, DateTimeOffset.Now) : null,
            last == default ? null : last.At, last == default ? null : last.Result);
    }).ToList();
}

public static class Schedule
{
    public static DateTimeOffset? Next(JobOptions job, DateTimeOffset from)
    {
        var zone = string.IsNullOrWhiteSpace(job.TimeZone) ? TimeZoneInfo.Local : TimeZoneInfo.FindSystemTimeZoneById(job.TimeZone);
        return CronExpression.Parse(job.Schedule).GetNextOccurrence(from, zone);
    }
}

/// <summary>Sleeps until the next due job, starts it in the background, repeats.</summary>
public sealed class Scheduler(AgentContext ctx, JobDispatcher dispatcher, ILogger<Scheduler> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var jobs = ctx.Options.Jobs.Where(j => j.Enabled).ToList();
        if (jobs.Count == 0)
        {
            logger.LogWarning("No enabled jobs configured");
            return;
        }

        var next = jobs.ToDictionary(j => j.Name, j => Schedule.Next(j, DateTimeOffset.Now));
        foreach (var (name, at) in next) logger.LogInformation("Job {Job} next runs at {At:yyyy-MM-dd HH:mm zzz}", name, at);

        while (!stoppingToken.IsCancellationRequested)
        {
            var due = next.Where(kv => kv.Value is not null).OrderBy(kv => kv.Value).FirstOrDefault();
            if (due.Value is null) return;

            var wait = due.Value.Value - DateTimeOffset.Now;
            if (wait > TimeSpan.Zero) await Task.Delay(wait < TimeSpan.FromHours(1) ? wait : TimeSpan.FromHours(1), stoppingToken);
            if (DateTimeOffset.Now < due.Value.Value) continue; // woke up early (long waits are chunked)

            var job = jobs.First(j => j.Name == due.Key);
            next[job.Name] = Schedule.Next(job, DateTimeOffset.Now.AddSeconds(1));
            _ = Task.Run(async () =>
            {
                var (started, _) = await dispatcher.RunAsync(job, stoppingToken);
                if (!started) logger.LogWarning("Job {Job} is still running from its previous schedule; skipped this occurrence", job.Name);
            }, stoppingToken);
        }
    }
}
