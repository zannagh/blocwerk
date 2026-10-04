// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Globalization;
using Npgsql;

namespace Blocwerk.Core.Tests;

/// <summary>
/// Inserts a row by raw SQL into a table as it is at the migration the database is at: every required column without a
/// default gets a stand-in value of its type unless the test names it, so a data migration can be tested at the schema
/// of its day without a model of it.
/// </summary>
internal static class PostgresRows
{
    public static async Task<Guid> InsertAsync(string connectionString, string table, params (string Column, object? Value)[] values)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        var given = values.ToDictionary(v => v.Column, v => v.Value);
        var id = given.TryGetValue("Id", out var chosen) ? (Guid)chosen! : Guid.NewGuid();
        given["Id"] = id;

        foreach (var (name, type) in await RequiredColumnsAsync(connection, table))
        {
            given.TryAdd(name, StandIn(type));
        }

        var names = given.Keys.ToList();
        var sql = string.Format(
            CultureInfo.InvariantCulture,
            "INSERT INTO \"{0}\" ({1}) VALUES ({2})",
            table,
            string.Join(", ", names.Select(n => $"\"{n}\"")),
            string.Join(", ", names.Select((_, i) => $"@p{i}")));
        await using var insert = new NpgsqlCommand(sql, connection);
        for (var i = 0; i < names.Count; i++)
        {
            insert.Parameters.AddWithValue($"p{i}", given[names[i]] ?? DBNull.Value);
        }

        await insert.ExecuteNonQueryAsync();
        return id;
    }

    public static async Task<List<T>> QueryAsync<T>(string connectionString, string sql, Func<NpgsqlDataReader, T> read)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<T>();
        while (await reader.ReadAsync())
        {
            rows.Add(read(reader));
        }

        return rows;
    }

    private static async Task<List<(string Name, string Type)>> RequiredColumnsAsync(NpgsqlConnection connection, string table)
    {
        await using var columns = new NpgsqlCommand(
            "SELECT column_name, data_type FROM information_schema.columns WHERE table_schema = 'public' AND table_name = @t AND is_nullable = 'NO' AND column_default IS NULL AND is_identity = 'NO' AND is_generated = 'NEVER'",
            connection);
        columns.Parameters.AddWithValue("t", table);
        await using var reader = await columns.ExecuteReaderAsync();
        var required = new List<(string, string)>();
        while (await reader.ReadAsync())
        {
            required.Add((reader.GetString(0), reader.GetString(1)));
        }

        return required;
    }

    private static object StandIn(string type) => type switch
    {
        "uuid" => Guid.NewGuid(),
        "boolean" => false,
        "integer" or "smallint" => 0,
        "bigint" => 0L,
        "double precision" or "real" => 0.0,
        "numeric" => 0m,
        "timestamp with time zone" => DateTimeOffset.UtcNow,
        "bytea" => new byte[] { 0 },
        _ => Guid.NewGuid().ToString("N")[..12],
    };
}
