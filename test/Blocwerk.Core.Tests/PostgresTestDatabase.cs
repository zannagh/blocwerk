// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Blocwerk.Core.Tests;

/// <summary>
/// A throwaway database on a real PostgreSQL server (connection string in <c>BLOCWERK_TEST_PG</c>, CI: the
/// <c>postgres:17</c> service container). The first one migrates a template with the real EF migrations
/// (<c>Database.Migrate()</c>, so the migration SQL runs too); every test then gets its own copy of it and drops it again.
/// Only ever creates and drops databases named <c>blocwerk_test_*</c>, through the server's <c>postgres</c> database.
/// </summary>
public sealed class PostgresTestDatabase : IDisposable
{
    public const string EnvVar = "BLOCWERK_TEST_PG";
    private const string Prefix = "blocwerk_test_";

    // Creating databases from one template cannot overlap (the template must be unused while it is copied).
    private static readonly object Gate = new();
    private static string? template;

    private PostgresTestDatabase(string name, string connectionString)
    {
        Name = name;
        ConnectionString = connectionString;
    }

    /// <summary>Gets a value indicating whether a server is configured.</summary>
    public static bool Enabled => !string.IsNullOrWhiteSpace(Server);

    /// <summary>Gets why the Postgres tests are skipped, or null when they run.</summary>
    public static string? SkipReason => Enabled ? null : $"Set {EnvVar} to a PostgreSQL connection string to run the Postgres tests.";

    public string Name { get; }

    public string ConnectionString { get; }

    private static string? Server => Environment.GetEnvironmentVariable(EnvVar);

    /// <summary>A migrated database, copied from the template.</summary>
    public static PostgresTestDatabase Create()
    {
        lock (Gate)
        {
            template ??= MigratedTemplate();
            var name = Prefix + Guid.NewGuid().ToString("N");
            Admin($"CREATE DATABASE \"{name}\" TEMPLATE \"{template}\"");
            return new PostgresTestDatabase(name, For(name));
        }
    }

    /// <summary>An empty database with no schema at all, for the migration chain itself.</summary>
    public static PostgresTestDatabase CreateEmpty()
    {
        var name = Prefix + Guid.NewGuid().ToString("N");
        Admin($"CREATE DATABASE \"{name}\"");
        return new PostgresTestDatabase(name, For(name));
    }

    /// <summary>A context over this database, with the interceptors if given.</summary>
    public BlocwerkDbContext CreateContext(params Microsoft.EntityFrameworkCore.Diagnostics.IInterceptor[] interceptors) =>
        TestDb.Create(ConnectionString, interceptors);

    public void Dispose() => Drop(Name);

    private static string MigratedTemplate()
    {
        var name = Prefix + "template_" + Guid.NewGuid().ToString("N");
        Admin($"CREATE DATABASE \"{name}\"");
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Drop(name);
        using var db = TestDb.Create(For(name));
        db.Database.Migrate();
        NpgsqlConnection.ClearAllPools();
        return name;
    }

    private static string For(string database) =>
        new NpgsqlConnectionStringBuilder(Server) { Database = database, Pooling = false }.ConnectionString;

    private static void Drop(string name) => Admin($"DROP DATABASE IF EXISTS \"{name}\" WITH (FORCE)");

    private static void Admin(string sql)
    {
        if (!sql.Contains(Prefix, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Refusing to touch a database that is not a Blocwerk test database.");
        }

        using var connection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(Server) { Database = "postgres", Pooling = false }.ConnectionString);
        connection.Open();
        using var command = new NpgsqlCommand(sql, connection);
        command.ExecuteNonQuery();
    }
}

/// <summary>A <c>[Fact]</c> that needs the Postgres server of <see cref="PostgresTestDatabase"/> and is skipped without it.</summary>
public sealed class PostgresFactAttribute : FactAttribute
{
    public PostgresFactAttribute()
    {
        Skip = PostgresTestDatabase.SkipReason;
    }
}

/// <summary>Builds the test context for either provider, chosen by the connection string (Npgsql ones name a Host).</summary>
public static class TestDb
{
    public static bool IsPostgres(string connectionString) =>
        connectionString.Contains("Host=", StringComparison.OrdinalIgnoreCase);

    public static BlocwerkDbContext Create(string connectionString, params Microsoft.EntityFrameworkCore.Diagnostics.IInterceptor[] interceptors)
    {
        var builder = new DbContextOptionsBuilder<BlocwerkDbContext>();
        if (IsPostgres(connectionString))
        {
            builder.UseNpgsql(connectionString);
        }
        else
        {
            builder.UseSqlite(connectionString);
        }

        if (interceptors.Length > 0)
        {
            builder.AddInterceptors(interceptors);
        }

        return IsPostgres(connectionString) ? new BlocwerkDbContext(builder.Options) : new SqliteBlocwerkDbContext(builder.Options);
    }
}
