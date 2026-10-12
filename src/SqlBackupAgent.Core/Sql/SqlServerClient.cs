using Dapper;
using Microsoft.Data.SqlClient;

namespace SqlBackupAgent.Core.Sql;

public sealed record ServerInfo(string ServerName, int EngineEdition, string ProductVersion, string DefaultDataPath, string DefaultLogPath)
{
    /// <summary>Express and LocalDB (engine edition 4) can't create compressed backups.</summary>
    public bool SupportsCompression => EngineEdition != 4;
}

public sealed record BackupSetInfo(DateTime Started, DateTime Finished, decimal FirstLsn, decimal LastLsn, decimal CheckpointLsn, decimal DatabaseBackupLsn, long SizeBytes);

public sealed record FileListEntry(string LogicalName, string Type, string PhysicalName);

/// <summary>Thin wrapper over SqlClient for the handful of commands the agent needs.</summary>
public sealed class SqlServerClient(string connectionString)
{
    public const int NoCurrentFullBackup = 3035; // differential without a base full
    public const int NoLogBackupPossible = 4214;  // log backup without a base full

    public async Task<ServerInfo> GetServerInfoAsync(CancellationToken ct)
    {
        await using var c = await OpenAsync(ct);
        return await c.QuerySingleAsync<ServerInfo>(new CommandDefinition("""
            SELECT ServerName = CAST(@@SERVERNAME AS nvarchar(256)),
                   EngineEdition = CAST(SERVERPROPERTY('EngineEdition') AS int),
                   ProductVersion = CAST(SERVERPROPERTY('ProductVersion') AS nvarchar(64)),
                   DefaultDataPath = CAST(SERVERPROPERTY('InstanceDefaultDataPath') AS nvarchar(4000)),
                   DefaultLogPath = CAST(SERVERPROPERTY('InstanceDefaultLogPath') AS nvarchar(4000))
            """, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<DatabaseInfo>> ListDatabasesAsync(CancellationToken ct)
    {
        await using var c = await OpenAsync(ct);
        var rows = await c.QueryAsync<DatabaseInfo>(new CommandDefinition("""
            SELECT Name = name, DatabaseId = database_id, State = state_desc, RecoveryModel = recovery_model_desc, IsReadOnly = is_read_only
            FROM sys.databases
            """, cancellationToken: ct));
        return rows.ToList();
    }

    public async Task ExecuteAsync(string sql, int timeoutSeconds, CancellationToken ct, string database = "master")
    {
        await using var c = await OpenAsync(ct, database);
        await using var cmd = new SqlCommand(sql, c) { CommandTimeout = timeoutSeconds };
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>The backupset row msdb recorded for a file we just wrote (LSNs drive restore planning).</summary>
    public async Task<BackupSetInfo> GetBackupSetAsync(string physicalFile, CancellationToken ct)
    {
        await using var c = await OpenAsync(ct);
        return await c.QuerySingleAsync<BackupSetInfo>(new CommandDefinition("""
            SELECT TOP (1) Started = bs.backup_start_date, Finished = bs.backup_finish_date,
                   FirstLsn = bs.first_lsn, LastLsn = bs.last_lsn, CheckpointLsn = bs.checkpoint_lsn,
                   DatabaseBackupLsn = ISNULL(bs.database_backup_lsn, 0),
                   SizeBytes = CAST(ISNULL(bs.compressed_backup_size, bs.backup_size) AS bigint)
            FROM msdb.dbo.backupset bs
            JOIN msdb.dbo.backupmediafamily mf ON mf.media_set_id = bs.media_set_id
            WHERE mf.physical_device_name = @physicalFile
            ORDER BY bs.backup_set_id DESC
            """, new { physicalFile }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<FileListEntry>> FileListAsync(string file, CancellationToken ct)
    {
        await using var c = await OpenAsync(ct);
        await using var cmd = new SqlCommand(TSql.FileListOnly(file), c) { CommandTimeout = 300 };
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var list = new List<FileListEntry>();
        while (await reader.ReadAsync(ct))
            list.Add(new FileListEntry(reader.GetString(reader.GetOrdinal("LogicalName")), reader.GetString(reader.GetOrdinal("Type")),
                reader.GetString(reader.GetOrdinal("PhysicalName"))));
        return list;
    }

    public async Task<T> ScalarAsync<T>(string sql, object? args, CancellationToken ct, string database = "master")
    {
        await using var c = await OpenAsync(ct, database);
        return (await c.ExecuteScalarAsync<T>(new CommandDefinition(sql, args, commandTimeout: 0, cancellationToken: ct)))!;
    }

    private async Task<SqlConnection> OpenAsync(CancellationToken ct, string database = "master")
    {
        var builder = new SqlConnectionStringBuilder(connectionString) { InitialCatalog = database };
        var c = new SqlConnection(builder.ConnectionString);
        await c.OpenAsync(ct);
        return c;
    }
}
