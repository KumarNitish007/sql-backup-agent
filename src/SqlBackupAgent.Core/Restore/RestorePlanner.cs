using SqlBackupAgent.Core.Catalog;

namespace SqlBackupAgent.Core.Restore;

public sealed record RestoreStep(BackupRecord Backup, bool StopAtApplies);

public sealed record RestorePlan(string Database, DateTime? PointInTime, IReadOnlyList<RestoreStep> Steps)
{
    public DateTime RestoresTo => PointInTime ?? Steps[^1].Backup.Finished;
}

public sealed class RestorePlanningException(string message) : Exception(message);

/// <summary>
/// Works out which backups restore a database to the latest state or to a point in time:
/// <list type="number">
/// <item>the newest full backup that finished before the target,</item>
/// <item>the newest differential based on that full (matched by LSN, not by time),</item>
/// <item>an unbroken sequence of log backups from there until the target.</item>
/// </list>
/// Gaps in the log chain are reported instead of producing a restore that would fail halfway through.
/// </summary>
public static class RestorePlanner
{
    public static RestorePlan Plan(string database, IEnumerable<BackupRecord> history, DateTime? pointInTime = null)
    {
        var backups = history
            .Where(b => b.Database.Equals(database, StringComparison.OrdinalIgnoreCase) && b.DeletedUtc is null)
            .ToList();
        var target = pointInTime ?? DateTime.MaxValue;

        var full = backups
            .Where(b => b.Type == BackupType.Full && b.Finished <= target)
            .OrderByDescending(b => b.Finished).ThenByDescending(b => b.LastLsn) // backups can finish in the same second
            .FirstOrDefault()
            ?? throw new RestorePlanningException(pointInTime is null
                ? $"No full backup of {database} is available."
                : $"No full backup of {database} finished before {pointInTime:yyyy-MM-dd HH:mm:ss}.");

        // Copy-only fulls don't reset the differential base, so no differential can be applied on top of one.
        var diff = full.CopyOnly ? null : backups
            .Where(b => b.Type == BackupType.Differential && !b.CopyOnly && b.DatabaseBackupLsn == full.CheckpointLsn && b.Finished <= target)
            .OrderByDescending(b => b.Finished).ThenByDescending(b => b.LastLsn)
            .FirstOrDefault();

        var steps = new List<RestoreStep> { new(full, false) };
        if (diff is not null) steps.Add(new RestoreStep(diff, false));

        var lastLsn = (diff ?? full).LastLsn;
        var logs = backups.Where(b => b.Type == BackupType.Log && b.LastLsn > lastLsn).OrderBy(b => b.FirstLsn).ToList();

        var reachedTarget = pointInTime is null;
        foreach (var log in logs)
        {
            if (log.FirstLsn > lastLsn)
            {
                if (pointInTime is null) break; // latest restorable state ends at the gap
                throw new RestorePlanningException(
                    $"Log chain for {database} is broken: no log backup covers LSN {lastLsn} " +
                    $"(next available starts at {log.FirstLsn}, {log.Finished:yyyy-MM-dd HH:mm:ss}).");
            }

            steps.Add(new RestoreStep(log, pointInTime is not null));
            lastLsn = log.LastLsn;

            if (log.Finished >= target)
            {
                reachedTarget = true;
                break;
            }
        }

        if (!reachedTarget)
        {
            var coveredUntil = steps[^1].Backup.Finished;
            if (pointInTime > coveredUntil)
                throw new RestorePlanningException(
                    $"{database} can only be restored up to {coveredUntil:yyyy-MM-dd HH:mm:ss}; no later log backup exists.");
        }

        return new RestorePlan(database, pointInTime, steps);
    }
}
