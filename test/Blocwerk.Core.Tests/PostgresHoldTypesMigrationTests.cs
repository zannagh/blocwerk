// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Enums;
using Blocwerk.Core.Migrations;

namespace Blocwerk.Core.Tests;

/// <summary>
/// <see cref="BoulderHoldTypesSet"/> on a real PostgreSQL with the values production holds: every stored dominant hold
/// type becomes its flag, none stays none, and Down maps a single grip back while a set it cannot express becomes none.
/// </summary>
[Trait("Db", "Postgres")]
public class PostgresHoldTypesMigrationTests
{
    [PostgresFact]
    public async Task Up_CopiesEveryDominantType_ToItsFlag_AndDown_MapsSingleGripsBack()
    {
        using var database = PostgresTestDatabase.CreateEmpty();
        await PostgresMigrationTests.MigrateBeforeAsync(database, nameof(BoulderHoldTypesSet));
        var user = await PostgresRows.InsertAsync(database.ConnectionString, "Users", ("Identifier", "u@test"), ("DisplayName", "U"));
        var wall = await PostgresRows.InsertAsync(database.ConnectionString, "Walls", ("Name", "W"), ("OwnerId", user));
        var boulders = new Dictionary<Guid, HoldHandType?>();
        foreach (var dominant in Enum.GetValues<HoldHandType>().Select(t => (HoldHandType?)t).Append(null))
        {
            var id = await PostgresRows.InsertAsync(
                database.ConnectionString, "Boulders", ("WallId", wall), ("CreatedByUserId", user), ("Name", $"B {dominant}"), ("DominantHoldType", (int?)dominant));
            boulders[id] = dominant;
        }

        await PostgresMigrationTests.MigrateToAsync(database, nameof(BoulderHoldTypesSet));

        var up = await ReadAsync(database, "HoldTypes", r => (BoulderHoldType)r.GetInt32(1));
        Assert.All(boulders, b => Assert.Equal(b.Value is { } t ? Enum.Parse<BoulderHoldType>(t.ToString()) : BoulderHoldType.None, up[b.Key]));

        var several = boulders.First(b => b.Value == HoldHandType.Crimp).Key;
        var hookOnly = boulders.First(b => b.Value == HoldHandType.Jug).Key;
        await SetAsync(database, several, BoulderHoldType.Crimp | BoulderHoldType.HeelHook);
        await SetAsync(database, hookOnly, BoulderHoldType.ToeHook);

        await PostgresMigrationTests.MigrateBeforeAsync(database, nameof(BoulderHoldTypesSet));

        var down = await ReadAsync(database, "DominantHoldType", r => r.IsDBNull(1) ? (HoldHandType?)null : (HoldHandType)r.GetInt32(1));
        Assert.All(boulders.Where(b => b.Key != several && b.Key != hookOnly), b => Assert.Equal(b.Value, down[b.Key]));
        Assert.Null(down[several]);
        Assert.Null(down[hookOnly]);
    }

    private static async Task<Dictionary<Guid, T>> ReadAsync<T>(PostgresTestDatabase database, string column, Func<Npgsql.NpgsqlDataReader, T> read) =>
        (await PostgresRows.QueryAsync(database.ConnectionString, $"SELECT \"Id\", \"{column}\" FROM \"Boulders\"", r => (Id: r.GetGuid(0), Value: read(r))))
            .ToDictionary(r => r.Id, r => r.Value);

    private static Task SetAsync(PostgresTestDatabase database, Guid boulder, BoulderHoldType types) =>
        PostgresRows.QueryAsync(
            database.ConnectionString,
            $"UPDATE \"Boulders\" SET \"HoldTypes\" = {(int)types} WHERE \"Id\" = '{boulder}' RETURNING \"Id\"",
            r => r.GetGuid(0));
}
