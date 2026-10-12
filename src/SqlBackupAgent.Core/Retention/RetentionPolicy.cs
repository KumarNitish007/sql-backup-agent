using SqlBackupAgent.Core.Catalog;

namespace SqlBackupAgent.Core.Retention;

/// <summary>
/// Decides which backups can be deleted without losing the ability to restore to any point in the retention window.
/// <para>
/// Deleting "everything older than N days" is a classic mistake: the full backup from N+3 days ago may be the base
/// that today's differentials and the window's log chain depend on. Instead, per database, the newest full backup that
/// finished before the window start is kept as the <i>anchor</i>; only backups older than the anchor are removed.
/// The newest <see cref="RetentionOptions.KeepLastFull"/> full backups are always kept.
/// </para>
/// </summary>
public static class RetentionPolicy
{
    public static IReadOnlyList<BackupRecord> SelectForDeletion(IEnumerable<BackupRecord> history, RetentionOptions options, DateTime now)
    {
        var windowStart = now.AddDays(-options.KeepDays);
        var doomed = new List<BackupRecord>();

        foreach (var database in history.Where(b => b.DeletedUtc is null).GroupBy(b => (b.Server, b.Database)))
        {
            var fulls = database.Where(b => b.Type == BackupType.Full && !b.CopyOnly).OrderByDescending(b => b.Finished).ToList();
            var protectedFulls = fulls.Take(Math.Max(1, options.KeepLastFull)).ToHashSet();

            var anchor = fulls.FirstOrDefault(b => b.Finished <= windowStart);
            if (anchor is null) continue; // the whole history is inside the window

            doomed.AddRange(database.Where(b =>
                b.Finished < anchor.Finished &&
                !protectedFulls.Contains(b) &&
                // a log that finished during the anchor full can still extend past it - the planner would need it
                !(b.Type == BackupType.Log && b.LastLsn > anchor.LastLsn)));
        }

        return doomed.OrderBy(b => b.Finished).ToList();
    }
}
