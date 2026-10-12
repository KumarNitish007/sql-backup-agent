namespace SqlBackupAgent.Core.Catalog;

/// <summary>
/// One backup file the agent produced. LSNs come from msdb.dbo.backupset right after the backup and
/// are what the restore planner uses to chain full → differential → log backups.
/// Times are SQL Server local time, matching msdb and what RESTORE ... STOPAT expects.
/// </summary>
public sealed record BackupRecord
{
    public long Id { get; init; }
    public required string Job { get; init; }
    public required string Server { get; init; }
    public required string Database { get; init; }
    public required BackupType Type { get; init; }
    public required string FileName { get; init; }
    public DateTime Started { get; init; }
    public DateTime Finished { get; init; }
    public long SizeBytes { get; init; }
    public string? Sha256 { get; init; }
    public decimal FirstLsn { get; init; }
    public decimal LastLsn { get; init; }
    public decimal CheckpointLsn { get; init; }

    /// <summary>For differential and log backups: checkpoint LSN of the full backup they are based on.</summary>
    public decimal DatabaseBackupLsn { get; init; }

    public bool CopyOnly { get; init; }
    public bool Verified { get; init; }

    /// <summary>Where copies live, e.g. "local:D:\Backups\...", "azure:prod/SRV/Sales/full/...".</summary>
    public IReadOnlyList<string> Locations { get; init; } = [];

    /// <summary>Path of the staged file as seen by SQL Server, while it still exists.</summary>
    public string? StagedServerPath { get; init; }

    public DateTime RecordedUtc { get; init; } = DateTime.UtcNow;
    public DateTime? DeletedUtc { get; init; }
    public string? Note { get; init; }
}

public enum RunStatus
{
    Succeeded,
    Warning,
    Failed,
    Skipped
}

/// <summary>Outcome of one job for one database (a backup or a restore drill).</summary>
public sealed record RunRecord
{
    public long Id { get; init; }
    public required string Job { get; init; }
    public required string Kind { get; init; }
    public required string Database { get; init; }
    public RunStatus Status { get; init; }
    public DateTime StartedUtc { get; init; }
    public DateTime FinishedUtc { get; init; }
    public string? Message { get; init; }
    public long? BackupId { get; init; }
}
