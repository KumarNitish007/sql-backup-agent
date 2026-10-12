using System.Globalization;
using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;

namespace SqlBackupAgent.Core.Catalog;

/// <summary>
/// The agent's own history in a local SQLite file: every backup (with LSNs and storage locations) and every run.
/// It is what makes restore planning possible even when msdb history has been purged or the server is gone.
/// LSNs are numeric(25,0) in SQL Server, beyond SQLite's REAL precision, so they are stored as text.
/// </summary>
public sealed class BackupCatalog
{
    private readonly string _connectionString;

    public BackupCatalog(string stateDirectory)
    {
        Directory.CreateDirectory(stateDirectory);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(stateDirectory, "catalog.db") }.ToString();
        using var c = Open();
        c.Execute("""
            PRAGMA journal_mode = WAL;
            CREATE TABLE IF NOT EXISTS backups (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                job TEXT NOT NULL, server TEXT NOT NULL, database TEXT NOT NULL, type TEXT NOT NULL,
                file_name TEXT NOT NULL, started TEXT NOT NULL, finished TEXT NOT NULL, size_bytes INTEGER NOT NULL,
                sha256 TEXT, first_lsn TEXT NOT NULL, last_lsn TEXT NOT NULL, checkpoint_lsn TEXT NOT NULL,
                database_backup_lsn TEXT NOT NULL, copy_only INTEGER NOT NULL, verified INTEGER NOT NULL,
                locations TEXT NOT NULL, staged_server_path TEXT, recorded_utc TEXT NOT NULL, deleted_utc TEXT, note TEXT);
            CREATE INDEX IF NOT EXISTS ix_backups_db ON backups(server, database, finished);
            CREATE TABLE IF NOT EXISTS runs (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                job TEXT NOT NULL, kind TEXT NOT NULL, database TEXT NOT NULL, status TEXT NOT NULL,
                started_utc TEXT NOT NULL, finished_utc TEXT NOT NULL, message TEXT, backup_id INTEGER);
            CREATE INDEX IF NOT EXISTS ix_runs_started ON runs(started_utc);
            """);
    }

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(_connectionString);
        c.Open();
        return c;
    }

    public long Add(BackupRecord b)
    {
        using var c = Open();
        return c.ExecuteScalar<long>("""
            INSERT INTO backups (job, server, database, type, file_name, started, finished, size_bytes, sha256, first_lsn, last_lsn,
                checkpoint_lsn, database_backup_lsn, copy_only, verified, locations, staged_server_path, recorded_utc, note)
            VALUES (@Job, @Server, @Database, @Type, @FileName, @Started, @Finished, @SizeBytes, @Sha256, @FirstLsn, @LastLsn,
                @CheckpointLsn, @DatabaseBackupLsn, @CopyOnly, @Verified, @Locations, @StagedServerPath, @RecordedUtc, @Note);
            SELECT last_insert_rowid();
            """, new
        {
            b.Job, b.Server, b.Database, Type = b.Type.ToString(), b.FileName, Started = Iso(b.Started), Finished = Iso(b.Finished),
            b.SizeBytes, b.Sha256, FirstLsn = Lsn(b.FirstLsn), LastLsn = Lsn(b.LastLsn), CheckpointLsn = Lsn(b.CheckpointLsn),
            DatabaseBackupLsn = Lsn(b.DatabaseBackupLsn), b.CopyOnly, b.Verified, Locations = JsonSerializer.Serialize(b.Locations),
            b.StagedServerPath, RecordedUtc = Iso(b.RecordedUtc), b.Note
        });
    }

    public void MarkDeleted(long id, DateTime utc)
    {
        using var c = Open();
        c.Execute("UPDATE backups SET deleted_utc = @utc WHERE id = @id", new { id, utc = Iso(utc) });
    }

    public void ClearStaged(long id)
    {
        using var c = Open();
        c.Execute("UPDATE backups SET staged_server_path = NULL WHERE id = @id", new { id });
    }

    public IReadOnlyList<BackupRecord> Backups(string? server = null, string? database = null, bool includeDeleted = false, int limit = 10_000)
    {
        using var c = Open();
        var rows = c.Query<Row>("""
            SELECT * FROM backups
            WHERE (@server IS NULL OR server = @server COLLATE NOCASE)
              AND (@database IS NULL OR database = @database COLLATE NOCASE)
              AND (@includeDeleted = 1 OR deleted_utc IS NULL)
            ORDER BY finished DESC, id DESC LIMIT @limit
            """, new { server, database, includeDeleted, limit });
        return rows.Select(r => r.ToRecord()).ToList();
    }

    public long AddRun(RunRecord r)
    {
        using var c = Open();
        return c.ExecuteScalar<long>("""
            INSERT INTO runs (job, kind, database, status, started_utc, finished_utc, message, backup_id)
            VALUES (@Job, @Kind, @Database, @Status, @StartedUtc, @FinishedUtc, @Message, @BackupId);
            SELECT last_insert_rowid();
            """, new { r.Job, r.Kind, r.Database, Status = r.Status.ToString(), StartedUtc = Iso(r.StartedUtc), FinishedUtc = Iso(r.FinishedUtc), r.Message, r.BackupId });
    }

    public IReadOnlyList<RunRecord> Runs(int limit = 200, string? job = null)
    {
        using var c = Open();
        return c.Query<RunRow>("SELECT * FROM runs WHERE (@job IS NULL OR job = @job) ORDER BY id DESC LIMIT @limit", new { limit, job })
            .Select(r => new RunRecord
            {
                Id = r.id, Job = r.job, Kind = r.kind, Database = r.database, Status = Enum.Parse<RunStatus>(r.status),
                StartedUtc = Parse(r.started_utc), FinishedUtc = Parse(r.finished_utc), Message = r.message, BackupId = r.backup_id
            })
            .ToList();
    }

    private static string Iso(DateTime d) => d.ToString("O", CultureInfo.InvariantCulture);
    private static DateTime Parse(string s) => DateTime.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    private static string Lsn(decimal d) => d.ToString(CultureInfo.InvariantCulture);

#pragma warning disable IDE1006 // column names
    private sealed record Row(long id, string job, string server, string database, string type, string file_name, string started,
        string finished, long size_bytes, string? sha256, string first_lsn, string last_lsn, string checkpoint_lsn, string database_backup_lsn,
        long copy_only, long verified, string locations, string? staged_server_path, string recorded_utc, string? deleted_utc, string? note)
    {
        public BackupRecord ToRecord() => new()
        {
            Id = id, Job = job, Server = server, Database = database, Type = Enum.Parse<BackupType>(type), FileName = file_name,
            Started = Parse(started), Finished = Parse(finished), SizeBytes = size_bytes, Sha256 = sha256,
            FirstLsn = decimal.Parse(first_lsn, CultureInfo.InvariantCulture), LastLsn = decimal.Parse(last_lsn, CultureInfo.InvariantCulture),
            CheckpointLsn = decimal.Parse(checkpoint_lsn, CultureInfo.InvariantCulture),
            DatabaseBackupLsn = decimal.Parse(database_backup_lsn, CultureInfo.InvariantCulture),
            CopyOnly = copy_only != 0, Verified = verified != 0, Locations = JsonSerializer.Deserialize<List<string>>(locations) ?? [],
            StagedServerPath = staged_server_path, RecordedUtc = Parse(recorded_utc), DeletedUtc = deleted_utc is null ? null : Parse(deleted_utc), Note = note
        };
    }

    private sealed record RunRow(long id, string job, string kind, string database, string status, string started_utc, string finished_utc,
        string? message, long? backup_id);
#pragma warning restore IDE1006
}
