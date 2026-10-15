using Npgsql;

namespace PharmaOS.IntegrationTests;

/// <summary>
/// Creates a throw-away database from database/schema/*.sql (the same DDL the installer loads) and
/// two login roles that mirror production: the application login (RLS enforced, no audit access)
/// and the auditor login (audit read only).
/// Requires PHARMAOS_TEST_PG: a superuser connection string, e.g.
/// "Host=localhost;Username=postgres;Password=postgres". The tests fail, rather than skip, without it.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    /// <summary>
    /// Test classes using this fixture share one collection so they run one after another: the DDL creates
    /// cluster-wide roles, and two fixtures loading it at once on a fresh server race on CREATE ROLE.
    /// </summary>
    public const string Collection = "postgres";

    private const string AppPassword = "it-app-password";
    private const string AuditorPassword = "it-auditor-password";

    private readonly string _admin = Environment.GetEnvironmentVariable("PHARMAOS_TEST_PG")
        ?? throw new InvalidOperationException(
            "Set PHARMAOS_TEST_PG to a PostgreSQL 16 superuser connection string to run integration tests.");

    public string DatabaseName { get; } = "pharmaos_it_" + Guid.NewGuid().ToString("N")[..10];
    public string AppConnectionString { get; private set; } = "";
    public string AuditConnectionString { get; private set; } = "";

    /// <summary>Superuser on the test database, for assertions that must see past RLS.</summary>
    public string AdminConnectionString { get; private set; } = "";

    public async Task InitializeAsync()
    {
        await using (var admin = new NpgsqlConnection(_admin))
        {
            await admin.OpenAsync();
            await Exec(admin, $"CREATE DATABASE {DatabaseName}");
        }

        var dbAdmin = new NpgsqlConnectionStringBuilder(_admin) { Database = DatabaseName, Pooling = false };
        await using (var conn = new NpgsqlConnection(dbAdmin.ConnectionString))
        {
            await conn.OpenAsync();
            foreach (var file in Directory.GetFiles(FindSchemaDirectory(), "0*.sql").Order(StringComparer.Ordinal))
                await Exec(conn, await File.ReadAllTextAsync(file));

            // Roles are cluster-wide: create once, reset passwords every run.
            await Exec(conn, $"""
                DO $$ BEGIN
                  IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'pharmaos_app_login') THEN
                    CREATE ROLE pharmaos_app_login LOGIN IN ROLE pharmaos_app;
                  END IF;
                  IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'pharmaos_auditor_login') THEN
                    CREATE ROLE pharmaos_auditor_login LOGIN IN ROLE pharmaos_auditor;
                  END IF;
                END $$;
                ALTER ROLE pharmaos_app_login PASSWORD '{AppPassword}';
                ALTER ROLE pharmaos_auditor_login PASSWORD '{AuditorPassword}';
                """);
        }

        AdminConnectionString = dbAdmin.ConnectionString;
        AppConnectionString = new NpgsqlConnectionStringBuilder(_admin)
            { Database = DatabaseName, Username = "pharmaos_app_login", Password = AppPassword }.ConnectionString;
        AuditConnectionString = new NpgsqlConnectionStringBuilder(_admin)
            { Database = DatabaseName, Username = "pharmaos_auditor_login", Password = AuditorPassword }.ConnectionString;
    }

    public async Task DisposeAsync()
    {
        NpgsqlConnection.ClearAllPools();
        await using var admin = new NpgsqlConnection(_admin);
        await admin.OpenAsync();
        await Exec(admin, $"DROP DATABASE IF EXISTS {DatabaseName} WITH (FORCE)");
    }

    private static async Task Exec(NpgsqlConnection conn, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, conn) { CommandTimeout = 300 };
        await cmd.ExecuteNonQueryAsync();
    }

    private static string FindSchemaDirectory()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "database", "schema");
            if (File.Exists(Path.Combine(candidate, "000_foundation.sql"))) return candidate;
        }
        throw new DirectoryNotFoundException("database/schema not found above " + AppContext.BaseDirectory);
    }
}

[CollectionDefinition(PostgresFixture.Collection)]
public sealed class PostgresCollection;
