using System.Globalization;
using SqlBackupAgent;
using SqlBackupAgent.Core;
using SqlBackupAgent.Core.Catalog;
using SqlBackupAgent.Core.Jobs;
using SqlBackupAgent.Core.Notifications;
using SqlBackupAgent.Core.Restore;

// Usage:
//   SqlBackupAgent                                     run as a service (scheduler + status page)
//   SqlBackupAgent run <job>                           run one job now and exit (exit code 1 on failure)
//   SqlBackupAgent plan <database> [--at "<time>"]     print the restore script (server local time)
//   SqlBackupAgent restore <database> --as <newName> [--at "<time>"] [--connection <name>]
//   SqlBackupAgent validate                            check the configuration and exit

var command = args.FirstOrDefault(a => !a.StartsWith('-'))?.ToLowerInvariant();
var commandArgs = command is null ? args : args.SkipWhile(a => !a.Equals(command, StringComparison.OrdinalIgnoreCase)).Skip(1).ToArray();

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = command is null ? args : [],
    ContentRootPath = AppContext.BaseDirectory // services start in system32; config lives next to the exe
});

builder.Host.UseWindowsService(o => o.ServiceName = "SqlBackupAgent");
builder.Host.UseSystemd();

var options = builder.Configuration.GetSection(AgentOptions.Section).Get<AgentOptions>() ?? new AgentOptions();
var stateDir = Path.GetFullPath(options.StateDirectory, builder.Environment.ContentRootPath);

builder.Services.AddSingleton(options);
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(_ => new BackupCatalog(stateDir));
builder.Services.AddSingleton(sp => new AgentContext(options, name => builder.Configuration.GetConnectionString(name),
    sp.GetRequiredService<BackupCatalog>(), sp.GetRequiredService<TimeProvider>()));
builder.Services.AddSingleton<BackupJobRunner>();
builder.Services.AddSingleton<RestoreExecutor>();
builder.Services.AddSingleton<RestoreDrillRunner>();
builder.Services.AddHttpClient<Notifier>();
builder.Services.AddSingleton(sp => new Notifier(options.Notifications,
    sp.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(Notifier)), sp.GetRequiredService<ILogger<Notifier>>()));
builder.Services.AddSingleton<JobDispatcher>();
if (command is null) builder.Services.AddHostedService<Scheduler>();

// The status page binds to localhost unless configured otherwise (Kestrel:Endpoints / ASPNETCORE_URLS).
if (string.IsNullOrEmpty(builder.Configuration["ASPNETCORE_URLS"]) && builder.Configuration.GetSection("Kestrel:Endpoints").GetChildren().Any() is false)
    builder.WebHost.UseUrls("http://localhost:5300");

var app = builder.Build();
var ctx = app.Services.GetRequiredService<AgentContext>();

var problems = ctx.Validate();
if (problems.Count > 0)
{
    foreach (var p in problems) app.Logger.LogError("Configuration error: {Problem}", p);
    if (command is not ("plan" or "validate")) return 2;
}

switch (command)
{
    case null:
        app.MapStatusApi();
        await app.RunAsync();
        return 0;

    case "validate":
        // Show exactly what each job will touch, so a pattern mistake is caught before it runs.
        foreach (var job in options.Jobs)
            Console.WriteLine($"{job.Name,-24} {job.Kind,-12} {(job.Kind == JobKind.Backup ? job.Type.ToString() : ""),-12} databases: {string.Join(", ", job.Databases)}" +
                              (job.Exclude.Count > 0 ? $"  exclude: {string.Join(", ", job.Exclude)}" : ""));
        Console.WriteLine(problems.Count == 0 ? "Configuration OK." : $"{problems.Count} problem(s) found.");
        return problems.Count == 0 ? 0 : 2;

    case "run":
    {
        var job = app.Services.GetRequiredService<JobDispatcher>().Find(commandArgs.FirstOrDefault() ?? "");
        if (job is null) { Console.Error.WriteLine("Usage: run <job name>"); return 2; }
        var (_, runs) = await app.Services.GetRequiredService<JobDispatcher>().RunAsync(job, CancellationToken.None);
        foreach (var r in runs) Console.WriteLine($"{r.Database,-30} {r.Status,-9} {r.Message}");
        return runs.Any(r => r.Status == RunStatus.Failed) ? 1 : 0;
    }

    case "plan":
    case "restore":
    {
        var database = commandArgs.FirstOrDefault(a => !a.StartsWith('-'));
        if (database is null) { Console.Error.WriteLine($"Usage: {command} <database> [--at \"yyyy-MM-dd HH:mm:ss\"] {(command == "restore" ? "--as <newName>" : "")}"); return 2; }
        var at = Option(commandArgs, "--at") is { } s ? DateTime.Parse(s, CultureInfo.InvariantCulture) : (DateTime?)null;
        var connection = Option(commandArgs, "--connection") ?? options.Jobs.FirstOrDefault()?.Connection ?? "Default";

        try
        {
            var plan = RestorePlanner.Plan(database, ctx.Catalog.Backups(database: database), at);
            var executor = app.Services.GetRequiredService<RestoreExecutor>();
            var sql = ctx.Sql(connection);
            if (command == "plan")
            {
                Console.WriteLine(await executor.BuildScriptAsync(sql, plan, database, CancellationToken.None, fetchMissing: false));
                return 0;
            }
            var target = Option(commandArgs, "--as") ?? throw new RestorePlanningException("restore needs --as <newName> (restoring over the live database is deliberately not a one-liner).");
            await executor.RestoreAsync(sql, plan, target, CancellationToken.None);
            Console.WriteLine($"Restored {database} as {target} to {plan.RestoresTo:yyyy-MM-dd HH:mm:ss}.");
            return 0;
        }
        catch (RestorePlanningException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    default:
        Console.Error.WriteLine($"Unknown command '{command}'. Commands: run, plan, restore, validate.");
        return 2;
}

static string? Option(string[] args, string name)
{
    var i = Array.FindIndex(args, a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

public partial class Program;
