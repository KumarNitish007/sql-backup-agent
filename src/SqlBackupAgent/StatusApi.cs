using System.Globalization;
using SqlBackupAgent.Core.Jobs;
using SqlBackupAgent.Core.Restore;

namespace SqlBackupAgent;

public static class StatusApi
{
    public static void MapStatusApi(this WebApplication app)
    {
        var apiKey = app.Configuration["Agent:StatusApiKey"];

        app.UseDefaultFiles();
        app.UseStaticFiles();

        app.MapGet("/health", () => Results.Ok("healthy"));
        app.MapGet("/api/jobs", (JobDispatcher d) => d.States());
        app.MapGet("/api/runs", (AgentContext ctx, int? limit) => ctx.Catalog.Runs(Math.Clamp(limit ?? 100, 1, 1000)));
        app.MapGet("/api/backups", (AgentContext ctx, string? database, int? limit) =>
            ctx.Catalog.Backups(database: database, limit: Math.Clamp(limit ?? 200, 1, 5000)).Select(b => new
            {
                b.Id, b.Job, b.Server, b.Database, Type = b.Type.ToString(), b.FileName, b.Started, b.Finished, b.SizeBytes,
                b.Verified, b.CopyOnly, b.Locations, b.Note
            }));

        // Restore plan preview (read-only): which files, in which order, up to when.
        app.MapGet("/api/restore-plan", (AgentContext ctx, string database, string? at) =>
        {
            try
            {
                var pit = string.IsNullOrWhiteSpace(at) ? (DateTime?)null : DateTime.Parse(at, CultureInfo.InvariantCulture);
                var plan = RestorePlanner.Plan(database, ctx.Catalog.Backups(database: database), pit);
                return Results.Ok(new
                {
                    plan.Database, RestoresTo = plan.RestoresTo,
                    Steps = plan.Steps.Select(s => new { Type = s.Backup.Type.ToString(), s.Backup.FileName, s.Backup.Finished, s.StopAtApplies })
                });
            }
            catch (Exception ex) when (ex is RestorePlanningException or FormatException)
            {
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status422UnprocessableEntity);
            }
        });

        // Manual trigger. If Agent:StatusApiKey is set, callers must send it in the X-Api-Key header.
        app.MapPost("/api/jobs/{name}/run", (string name, HttpRequest request, JobDispatcher dispatcher, IHostApplicationLifetime life) =>
        {
            if (!string.IsNullOrEmpty(apiKey) && request.Headers["X-Api-Key"] != apiKey) return Results.Unauthorized();
            var job = dispatcher.Find(name);
            if (job is null) return Results.NotFound();
            if (dispatcher.States().Any(s => s.Name == job.Name && s.Running)) return Results.Conflict(new { message = "Job is already running." });

            _ = Task.Run(() => dispatcher.RunAsync(job, life.ApplicationStopping));
            return Results.Accepted($"/api/jobs", new { message = $"Job '{job.Name}' started." });
        });
    }
}
