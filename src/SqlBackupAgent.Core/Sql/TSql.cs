using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace SqlBackupAgent.Core.Sql;

/// <summary>
/// T-SQL text building. BACKUP/RESTORE can't take the database name as a parameter, so names and paths
/// are escaped exactly the way QUOTENAME and N'' literals do; nothing user-supplied is concatenated raw.
/// </summary>
public static partial class TSql
{
    public static string QuoteName(string name) => "[" + name.Replace("]", "]]") + "]";

    public static string Literal(string value) => "N'" + value.Replace("'", "''") + "'";

    public static string Backup(string database, BackupType type, string file, bool compress, bool copyOnly, bool checksum = true)
    {
        var options = new List<string> { "INIT", "FORMAT", "SKIP" };
        if (type == BackupType.Differential) options.Add("DIFFERENTIAL");
        if (checksum) options.Add("CHECKSUM");
        if (compress) options.Add("COMPRESSION");
        if (copyOnly) options.Add("COPY_ONLY");
        options.Add("NAME = " + Literal($"SqlBackupAgent {type} {database}"));

        var verb = type == BackupType.Log ? "LOG" : "DATABASE";
        return $"BACKUP {verb} {QuoteName(database)} TO DISK = {Literal(file)} WITH {string.Join(", ", options)};";
    }

    public static string VerifyOnly(string file) => $"RESTORE VERIFYONLY FROM DISK = {Literal(file)} WITH CHECKSUM;";

    public static string FileListOnly(string file) => $"RESTORE FILELISTONLY FROM DISK = {Literal(file)};";

    public static string StopAt(DateTime serverTime) =>
        Literal(serverTime.ToString("yyyy-MM-ddTHH:mm:ss.fff", CultureInfo.InvariantCulture));

    /// <summary>File name: SERVER_Database_FULL_20261012_020000.bak (unsafe characters replaced).</summary>
    public static string BackupFileName(string server, string database, BackupType type, DateTime timestamp)
    {
        var kind = type switch { BackupType.Full => "FULL", BackupType.Differential => "DIFF", _ => "LOG" };
        var ext = type == BackupType.Log ? "trn" : "bak";
        return $"{Safe(server)}_{Safe(database)}_{kind}_{timestamp:yyyyMMdd_HHmmss}.{ext}";
    }

    /// <summary>Destination key: server/database/type/file - groups a database's history together in storage.</summary>
    public static string StorageKey(string server, string database, BackupType type, string fileName) =>
        $"{Safe(server)}/{Safe(database)}/{type.ToString().ToLowerInvariant()}/{fileName}";

    public static string Safe(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var c in value) sb.Append(char.IsLetterOrDigit(c) || c is '-' or '.' ? c : '_');
        return MultiUnderscore().Replace(sb.ToString(), "_");
    }

    [GeneratedRegex("_{2,}")]
    private static partial Regex MultiUnderscore();
}

public sealed record DatabaseInfo(string Name, int DatabaseId, string State, string RecoveryModel, bool IsReadOnly)
{
    public bool IsSystem => DatabaseId <= 4;
}

public static class DatabaseSelector
{
    /// <summary>
    /// Resolves job patterns to concrete online databases. tempdb is never selected; other system databases
    /// only with includeSystem. Patterns support * and ? wildcards, case-insensitively.
    /// </summary>
    public static IReadOnlyList<DatabaseInfo> Select(IEnumerable<DatabaseInfo> all, IReadOnlyList<string> include,
        IReadOnlyList<string> exclude, bool includeSystem) =>
        all.Where(d => !d.Name.Equals("tempdb", StringComparison.OrdinalIgnoreCase))
            .Where(d => d.State.Equals("ONLINE", StringComparison.OrdinalIgnoreCase))
            .Where(d => includeSystem || !d.IsSystem || include.Any(p => !HasWildcard(p) && p.Equals(d.Name, StringComparison.OrdinalIgnoreCase)))
            .Where(d => include.Any(p => Matches(d.Name, p)))
            .Where(d => !exclude.Any(p => Matches(d.Name, p)))
            .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static bool HasWildcard(string pattern) => pattern.Contains('*') || pattern.Contains('?');

    public static bool Matches(string name, string pattern) =>
        Regex.IsMatch(name, "^" + Regex.Escape(pattern).Replace(@"\*", ".*").Replace(@"\?", ".") + "$", RegexOptions.IgnoreCase);
}
