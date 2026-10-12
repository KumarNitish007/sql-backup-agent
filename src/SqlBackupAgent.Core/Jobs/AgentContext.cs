using SqlBackupAgent.Core.Catalog;
using SqlBackupAgent.Core.Destinations;
using SqlBackupAgent.Core.Sql;

namespace SqlBackupAgent.Core.Jobs;

/// <summary>Everything a job needs, resolved once from configuration.</summary>
public sealed class AgentContext
{
    private readonly Func<string, string?> _connectionStrings;

    public AgentContext(AgentOptions options, Func<string, string?> connectionStrings, BackupCatalog catalog, TimeProvider clock)
    {
        Options = options;
        _connectionStrings = connectionStrings;
        Catalog = catalog;
        Clock = clock;
        Destinations = options.Destinations.ToDictionary(
            kv => kv.Key, kv => DestinationFactory.Create(kv.Key, kv.Value, connectionStrings), StringComparer.OrdinalIgnoreCase);
    }

    public AgentOptions Options { get; }
    public BackupCatalog Catalog { get; }
    public TimeProvider Clock { get; }
    public IReadOnlyDictionary<string, IBackupDestination> Destinations { get; }

    public SqlServerClient Sql(string connectionName) =>
        new(_connectionStrings(connectionName) ?? throw new InvalidOperationException($"Connection string '{connectionName}' is not configured."));

    public IBackupDestination? DestinationFor(string location) => Destinations.Values.FirstOrDefault(d => d.Owns(location));

    /// <summary>Checks the configuration before anything runs, so mistakes surface at startup, not at 2 a.m.</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(Options.Staging.ServerPath)) errors.Add("Agent:Staging:ServerPath is required.");
        foreach (var job in Options.Jobs)
        {
            if (string.IsNullOrWhiteSpace(job.Name)) errors.Add("Every job needs a Name.");
            if (job.Databases.Count == 0) errors.Add($"Job '{job.Name}': Databases must list at least one name or pattern (use \"*\" explicitly for all).");
            if (job.Kind == JobKind.Backup && job.Destinations.Count == 0) errors.Add($"Job '{job.Name}': Destinations must list at least one destination.");
            if (_connectionStrings(job.Connection) is null) errors.Add($"Job '{job.Name}': connection string '{job.Connection}' is not configured.");
            foreach (var d in job.Destinations.Where(d => !Destinations.ContainsKey(d)))
                errors.Add($"Job '{job.Name}': unknown destination '{d}'.");
            try { Cronos.CronExpression.Parse(job.Schedule); }
            catch (Cronos.CronFormatException) { errors.Add($"Job '{job.Name}': invalid cron schedule '{job.Schedule}'."); }
        }
        foreach (var dup in Options.Jobs.GroupBy(j => j.Name, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            errors.Add($"Duplicate job name '{dup.Key}'.");
        return errors;
    }
}
