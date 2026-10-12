namespace SqlBackupAgent.Core;

public enum BackupType
{
    Full,
    Differential,
    Log
}

public enum JobKind
{
    /// <summary>Back up the selected databases.</summary>
    Backup,

    /// <summary>Restore each database's latest backup chain into a scratch database and run DBCC CHECKDB.</summary>
    RestoreDrill
}

/// <summary>Root configuration section "Agent".</summary>
public sealed class AgentOptions
{
    public const string Section = "Agent";

    public List<JobOptions> Jobs { get; set; } = [];
    public Dictionary<string, DestinationOptions> Destinations { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public StagingOptions Staging { get; set; } = new();
    public RetentionOptions Retention { get; set; } = new();
    public NotificationOptions Notifications { get; set; } = new();

    /// <summary>Folder for the agent's own catalog database (backup history, LSNs, drill results).</summary>
    public string StateDirectory { get; set; } = "state";

    public int MaxConcurrentJobs { get; set; } = 2;
}

public sealed class JobOptions
{
    public string Name { get; set; } = "";
    public JobKind Kind { get; set; } = JobKind.Backup;
    public bool Enabled { get; set; } = true;

    /// <summary>Name of an entry in ConnectionStrings (the connection string itself never lives in the job).</summary>
    public string Connection { get; set; } = "Default";

    /// <summary>
    /// Database names or wildcards ("*", "Sales*"). Required: an empty list is a configuration error, never "all".
    /// System databases need IncludeSystemDatabases.
    /// </summary>
    /// <remarks>
    /// Lists deliberately have no default values: the .NET configuration binder APPENDS configured items to a
    /// pre-populated list, so a default of ["*"] would silently turn ["Sales"] into ["*", "Sales"].
    /// </remarks>
    public List<string> Databases { get; set; } = [];
    public List<string> Exclude { get; set; } = [];
    public bool IncludeSystemDatabases { get; set; }

    public BackupType Type { get; set; } = BackupType.Full;

    /// <summary>Cron expression (5 fields), e.g. "0 2 * * *" = 02:00 daily.</summary>
    public string Schedule { get; set; } = "0 2 * * *";

    /// <summary>IANA or Windows time zone id for the schedule; defaults to the machine's local zone.</summary>
    public string? TimeZone { get; set; }

    public bool Compress { get; set; } = true;
    public bool Verify { get; set; } = true;
    public bool CopyOnly { get; set; }

    /// <summary>Keys of Agent:Destinations to copy each backup to. Required for backup jobs.</summary>
    public List<string> Destinations { get; set; } = [];

    /// <summary>Seconds; 0 = no timeout (large databases can take hours).</summary>
    public int CommandTimeoutSeconds { get; set; }
}

public sealed class StagingOptions
{
    /// <summary>Folder SQL Server writes backup files to, as seen by the SQL Server service.</summary>
    public string ServerPath { get; set; } = "";

    /// <summary>
    /// The same folder as seen by this agent. Only needed when they differ, e.g. SQL Server in a container
    /// writing to /backups that is mounted on the host at /srv/backups. Defaults to ServerPath.
    /// </summary>
    public string? AgentPath { get; set; }

    /// <summary>Delete the staged file once every destination has a verified copy.</summary>
    public bool DeleteAfterUpload { get; set; } = true;

    public string ToAgentPath(string serverFile)
    {
        if (string.IsNullOrEmpty(AgentPath)) return serverFile;
        var relative = serverFile[ServerPath.Length..].TrimStart('/', '\\');
        return Path.Combine(AgentPath, relative);
    }

    public string ServerFile(string fileName) =>
        ServerPath.Contains('/') && !ServerPath.Contains('\\')
            ? ServerPath.TrimEnd('/') + "/" + fileName // Linux SQL Server
            : Path.Combine(ServerPath, fileName);
}

/// <summary>
/// Keep everything needed to restore to any point in the last <see cref="KeepDays"/> days,
/// and never fewer than <see cref="KeepLastFull"/> full backups per database.
/// </summary>
public sealed class RetentionOptions
{
    public int KeepDays { get; set; } = 14;
    public int KeepLastFull { get; set; } = 2;
}

public sealed class DestinationOptions
{
    /// <summary>Folder | AzureBlob</summary>
    public string Type { get; set; } = "Folder";

    /// <summary>Folder: target directory (local disk or UNC share).</summary>
    public string? Path { get; set; }

    /// <summary>AzureBlob: container name.</summary>
    public string? Container { get; set; }

    /// <summary>AzureBlob: name of an entry in ConnectionStrings holding the storage connection string.</summary>
    public string? ConnectionStringName { get; set; }

    /// <summary>Optional key prefix, e.g. "prod/".</summary>
    public string? Prefix { get; set; }
}

public sealed class NotificationOptions
{
    public bool OnFailure { get; set; } = true;
    public bool OnSuccess { get; set; }

    /// <summary>Generic JSON webhook (works with Slack/Teams incoming webhooks). Supply the URL via environment variable.</summary>
    public string? WebhookUrl { get; set; }

    public EmailOptions? Email { get; set; }
}

public sealed class EmailOptions
{
    public string Host { get; set; } = "";
    public int Port { get; set; } = 587;
    public bool UseStartTls { get; set; } = true;
    public string? UserName { get; set; }

    /// <summary>Supply via environment variable Agent__Notifications__Email__Password, never in a file.</summary>
    public string? Password { get; set; }

    public string From { get; set; } = "";
    public List<string> To { get; set; } = [];
}
