// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Text.Json;
using Blocwerk.Core.Configuration;
using Blocwerk.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Browser.Seed;

/// <summary>
/// Migrates a throwaway database and seeds what the browser harness needs, then writes the ids and hashes as JSON.
/// Usage: <c>Blocwerk.Browser.Seed &lt;connection string&gt; &lt;wall-image storage dir&gt; &lt;output json&gt;</c>, or
/// <c>--reset &lt;maintenance connection&gt; &lt;database&gt;</c> (see <see cref="ResetAsync"/>).
/// Refuses any database whose name does not start with <c>bw_browser_</c>, so it can never write to a real one.
/// </summary>
public static class Program
{
    private const string DatabasePrefix = "bw_browser_";

    public static async Task<int> Main(string[] args)
    {
        if (args is ["--reset", var adminConnection, var name])
        {
            return await ResetAsync(adminConnection, name);
        }

        if (args.Length != 3)
        {
            Console.Error.WriteLine("usage: Blocwerk.Browser.Seed <connection string> <storage dir> <output json>");
            return 2;
        }

        var connectionString = args[0];
        var database = new Npgsql.NpgsqlConnectionStringBuilder(connectionString).Database ?? string.Empty;
        if (!database.StartsWith(DatabasePrefix, StringComparison.Ordinal))
        {
            Console.Error.WriteLine($"refusing to seed '{database}': the harness only seeds databases named {DatabasePrefix}*");
            return 2;
        }

        var options = new DbContextOptionsBuilder<BlocwerkDbContext>().UseNpgsql(connectionString).Options;
        await using (var migrate = new BlocwerkDbContext(options))
        {
            await migrate.Database.MigrateAsync();
        }

        var settings = new BlocwerkSettings();
        settings.WallImage.StoragePath = args[1];
        await using var db = new BlocwerkDbContext(options);
        db.CurrentUserId = Guid.Empty;
        var seeded = await new SeedWalls(db, settings).SeedAsync();

        var json = JsonSerializer.Serialize(seeded, new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        await File.WriteAllTextAsync(args[2], json);
        Console.WriteLine($"seeded {database}: {seeded.Walls.Count} walls, {seeded.Users.Count} users");
        return 0;
    }

    /// <summary>
    /// <c>--reset &lt;connection to a maintenance database&gt; &lt;name&gt;</c>: drops and creates the harness database, so a run
    /// against a server you provide starts from the same seed. Only a <c>bw_browser_*</c> name (letters, digits, underscores).
    /// </summary>
    private static async Task<int> ResetAsync(string adminConnection, string name)
    {
        if (!name.StartsWith(DatabasePrefix, StringComparison.Ordinal) || !name.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '_'))
        {
            Console.Error.WriteLine($"refusing to reset '{name}': the harness only resets databases named {DatabasePrefix}*");
            return 2;
        }

        await using var connection = new Npgsql.NpgsqlConnection(adminConnection);
        await connection.OpenAsync();
        foreach (var sql in new[] { $"DROP DATABASE IF EXISTS \"{name}\" WITH (FORCE)", $"CREATE DATABASE \"{name}\"" })
        {
            await using var command = new Npgsql.NpgsqlCommand(sql, connection);
            await command.ExecuteNonQueryAsync();
        }

        Console.WriteLine($"reset {name}");
        return 0;
    }
}
