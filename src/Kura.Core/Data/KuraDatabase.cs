using Dapper;
using Microsoft.Data.Sqlite;

namespace Kura.Core.Data;

/// <summary>Owns the SQLite file: opens pooled connections and applies embedded migrations.</summary>
public sealed class KuraDatabase
{
    private readonly string _connectionString;

    static KuraDatabase()
    {
        DefaultTypeMap.MatchNamesWithUnderscores = true;
        SqlMapper.AddTypeHandler(new DateTimeOffsetHandler());
        SqlMapper.AddTypeHandler(new BoolHandler());
    }

    public KuraDatabase(string path)
    {
        Path = path;
        var dir = System.IO.Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = true,
            ForeignKeys = true,
            DefaultTimeout = 30,
        }.ToString();
    }

    public string Path { get; }

    public SqliteConnection Open()
    {
        var c = new SqliteConnection(_connectionString);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "PRAGMA busy_timeout = 5000; PRAGMA synchronous = NORMAL;";
        cmd.ExecuteNonQuery();
        return c;
    }

    /// <summary>Enables WAL and runs pending migrations tracked by <c>PRAGMA user_version</c>.</summary>
    public int Migrate()
    {
        using var c = Open();
        c.Execute("PRAGMA journal_mode = WAL;");
        var current = c.ExecuteScalar<long>("PRAGMA user_version;");
        var applied = 0;
        foreach (var (version, sql) in LoadMigrations().Where(m => m.Version > current))
        {
            using var tx = c.BeginTransaction();
            c.Execute(sql, transaction: tx);
            c.Execute($"PRAGMA user_version = {version};", transaction: tx);
            tx.Commit();
            applied++;
        }
        return applied;
    }

    public int SchemaVersion
    {
        get { using var c = Open(); return (int)c.ExecuteScalar<long>("PRAGMA user_version;"); }
    }

    public string JournalMode
    {
        get { using var c = Open(); return c.ExecuteScalar<string>("PRAGMA journal_mode;") ?? ""; }
    }

    private static IEnumerable<(int Version, string Sql)> LoadMigrations()
    {
        var asm = typeof(KuraDatabase).Assembly;
        const string prefix = "Kura.Core.Data.Migrations.";
        return asm.GetManifestResourceNames()
            .Where(n => n.StartsWith(prefix, StringComparison.Ordinal) && n.EndsWith(".sql", StringComparison.Ordinal))
            .Select(n =>
            {
                var file = n[prefix.Length..];
                // Embedded resource names for files starting with a digit get a leading underscore.
                file = file.TrimStart('_');
                var version = int.Parse(file[..file.IndexOf('_')], System.Globalization.CultureInfo.InvariantCulture);
                using var s = asm.GetManifestResourceStream(n)!;
                using var r = new StreamReader(s);
                return (version, r.ReadToEnd());
            })
            .OrderBy(m => m.version);
    }

    private sealed class DateTimeOffsetHandler : SqlMapper.TypeHandler<DateTimeOffset>
    {
        public override void SetValue(System.Data.IDbDataParameter parameter, DateTimeOffset value) =>
            parameter.Value = value.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture);

        public override DateTimeOffset Parse(object value) =>
            DateTimeOffset.Parse((string)value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private sealed class BoolHandler : SqlMapper.TypeHandler<bool>
    {
        public override void SetValue(System.Data.IDbDataParameter parameter, bool value) => parameter.Value = value ? 1 : 0;
        public override bool Parse(object value) => Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture) != 0;
    }
}
