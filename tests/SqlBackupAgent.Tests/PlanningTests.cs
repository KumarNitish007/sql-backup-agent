using SqlBackupAgent.Core;
using SqlBackupAgent.Core.Catalog;
using SqlBackupAgent.Core.Restore;
using SqlBackupAgent.Core.Retention;
using Shouldly;

namespace SqlBackupAgent.Tests;

/// <summary>Builds realistic backup histories: LSNs advance, diffs point at their base full, logs chain end-to-start.</summary>
internal sealed class History
{
    private readonly List<BackupRecord> _items = [];
    private decimal _lsn = 1000;
    private decimal _lastFullCheckpoint;
    private decimal _logChainEnd;

    public IReadOnlyList<BackupRecord> Items => _items;

    public BackupRecord Full(DateTime finished, bool copyOnly = false)
    {
        var first = _lsn; _lsn += 100;
        var b = Add(BackupType.Full, finished, first, _lsn, checkpoint: first + 50, baseLsn: 0, copyOnly);
        if (!copyOnly) _lastFullCheckpoint = b.CheckpointLsn;
        if (_logChainEnd == 0) _logChainEnd = first;
        return b;
    }

    public BackupRecord Diff(DateTime finished)
    {
        var first = _lsn; _lsn += 100;
        return Add(BackupType.Differential, finished, first, _lsn, first + 50, _lastFullCheckpoint);
    }

    public BackupRecord Log(DateTime finished, bool gapBefore = false)
    {
        if (gapBefore) _logChainEnd += 7; // simulate a missing log backup
        var first = _logChainEnd; _lsn += 100; _logChainEnd = _lsn;
        return Add(BackupType.Log, finished, first, _lsn, 0, _lastFullCheckpoint);
    }

    private BackupRecord Add(BackupType type, DateTime finished, decimal first, decimal last, decimal checkpoint, decimal baseLsn, bool copyOnly = false)
    {
        var b = new BackupRecord
        {
            Id = _items.Count + 1, Job = "test", Server = "SRV", Database = "Sales", Type = type, FileName = $"{type}_{finished:HHmm}",
            Started = finished.AddMinutes(-1), Finished = finished, FirstLsn = first, LastLsn = last, CheckpointLsn = checkpoint,
            DatabaseBackupLsn = baseLsn, CopyOnly = copyOnly
        };
        _items.Add(b);
        return b;
    }
}

public class RestorePlannerTests
{
    private static readonly DateTime Day = new(2026, 10, 12);

    [Fact]
    public void Latest_restore_uses_newest_full_newest_matching_diff_and_following_logs()
    {
        var h = new History();
        h.Full(Day.AddHours(1));
        h.Log(Day.AddHours(2));
        var full = h.Full(Day.AddHours(3));
        h.Diff(Day.AddHours(5));
        var diff = h.Diff(Day.AddHours(7));
        var log1 = h.Log(Day.AddHours(8));
        var log2 = h.Log(Day.AddHours(9));

        var plan = RestorePlanner.Plan("Sales", h.Items);

        plan.Steps.Select(s => s.Backup).ShouldBe([full, diff, log1, log2]);
        plan.Steps.ShouldAllBe(s => !s.StopAtApplies);
        plan.RestoresTo.ShouldBe(Day.AddHours(9));
    }

    [Fact]
    public void Point_in_time_stops_at_the_first_log_covering_the_target()
    {
        var h = new History();
        var full = h.Full(Day.AddHours(1));
        var log1 = h.Log(Day.AddHours(2));
        var log2 = h.Log(Day.AddHours(3));
        h.Log(Day.AddHours(4));

        var plan = RestorePlanner.Plan("Sales", h.Items, Day.AddHours(2).AddMinutes(30));

        plan.Steps.Select(s => s.Backup).ShouldBe([full, log1, log2]);
        plan.Steps.Where(s => s.Backup.Type == BackupType.Log).ShouldAllBe(s => s.StopAtApplies);
    }

    [Fact]
    public void Differentials_taken_after_the_target_are_not_used()
    {
        var h = new History();
        var full = h.Full(Day.AddHours(1));
        var log = h.Log(Day.AddHours(2));
        h.Diff(Day.AddHours(3));

        RestorePlanner.Plan("Sales", h.Items, Day.AddHours(1).AddMinutes(30)).Steps.Select(s => s.Backup).ShouldBe([full, log]);
    }

    [Fact]
    public void A_gap_in_the_log_chain_is_reported_for_point_in_time()
    {
        var h = new History();
        h.Full(Day.AddHours(1));
        h.Log(Day.AddHours(2));
        h.Log(Day.AddHours(4), gapBefore: true);

        Should.Throw<RestorePlanningException>(() => RestorePlanner.Plan("Sales", h.Items, Day.AddHours(3)))
            .Message.ShouldContain("Log chain for Sales is broken");
    }

    [Fact]
    public void Latest_restore_stops_cleanly_at_a_gap()
    {
        var h = new History();
        var full = h.Full(Day.AddHours(1));
        var log = h.Log(Day.AddHours(2));
        h.Log(Day.AddHours(4), gapBefore: true);

        RestorePlanner.Plan("Sales", h.Items).Steps.Select(s => s.Backup).ShouldBe([full, log]);
    }

    [Fact]
    public void Target_before_the_first_full_is_impossible()
    {
        var h = new History();
        h.Full(Day.AddHours(5));

        Should.Throw<RestorePlanningException>(() => RestorePlanner.Plan("Sales", h.Items, Day.AddHours(1)))
            .Message.ShouldContain("No full backup of Sales finished before");
    }

    [Fact]
    public void Target_after_the_last_log_is_impossible()
    {
        var h = new History();
        h.Full(Day.AddHours(1));
        h.Log(Day.AddHours(2));

        Should.Throw<RestorePlanningException>(() => RestorePlanner.Plan("Sales", h.Items, Day.AddHours(6)))
            .Message.ShouldContain("can only be restored up to");
    }

    [Fact]
    public void Diffs_based_on_a_different_full_are_ignored()
    {
        var h = new History();
        h.Full(Day.AddHours(1));
        var orphanDiff = h.Diff(Day.AddHours(2));
        var copyOnlyFull = h.Full(Day.AddHours(3), copyOnly: true);

        // the diff's base is the first full, not the copy-only one
        RestorePlanner.Plan("Sales", h.Items).Steps.Select(s => s.Backup).ShouldBe([copyOnlyFull]);
        orphanDiff.DatabaseBackupLsn.ShouldNotBe(copyOnlyFull.CheckpointLsn);
    }

    [Fact]
    public void Deleted_backups_are_never_planned()
    {
        var h = new History();
        h.Full(Day.AddHours(1));
        var items = h.Items.Select(b => b with { DeletedUtc = DateTime.UtcNow }).ToList();

        Should.Throw<RestorePlanningException>(() => RestorePlanner.Plan("Sales", items));
    }
}

public class RetentionPolicyTests
{
    private static readonly DateTime Now = new(2026, 10, 31, 12, 0, 0);

    [Fact]
    public void Keeps_the_full_that_anchors_the_window_even_though_it_is_older_than_the_window()
    {
        var h = new History();
        var veryOld = h.Full(Now.AddDays(-30));
        var oldLog = h.Log(Now.AddDays(-29));
        var anchor = h.Full(Now.AddDays(-20));         // last full before the 14-day window
        var anchorLog = h.Log(Now.AddDays(-19));
        h.Diff(Now.AddDays(-10));
        h.Full(Now.AddDays(-1));

        var doomed = RetentionPolicy.SelectForDeletion(h.Items, new RetentionOptions { KeepDays = 14, KeepLastFull = 1 }, Now);

        doomed.ShouldBe([veryOld, oldLog]);
        doomed.ShouldNotContain(anchor);
        doomed.ShouldNotContain(anchorLog);
    }

    [Fact]
    public void Nothing_is_deleted_while_the_whole_history_is_inside_the_window()
    {
        var h = new History();
        h.Full(Now.AddDays(-5));
        h.Log(Now.AddDays(-4));

        RetentionPolicy.SelectForDeletion(h.Items, new RetentionOptions { KeepDays = 14 }, Now).ShouldBeEmpty();
    }

    [Fact]
    public void Keep_last_full_overrides_age()
    {
        var h = new History();
        var f1 = h.Full(Now.AddDays(-60));
        var f2 = h.Full(Now.AddDays(-50));
        var f3 = h.Full(Now.AddDays(-40));

        var doomed = RetentionPolicy.SelectForDeletion(h.Items, new RetentionOptions { KeepDays = 7, KeepLastFull = 2 }, Now);

        doomed.ShouldBe([f1]);
        doomed.ShouldNotContain(f2);
        doomed.ShouldNotContain(f3);
    }
}
