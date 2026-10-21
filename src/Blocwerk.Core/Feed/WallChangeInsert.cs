// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Blocwerk.Core.Feed;

/// <summary>
/// The raw-SQL insert of one <see cref="WallChange"/> behind <see cref="IWallFeed.AppendAsync"/>. Column names, value
/// conversions and the <c>jsonb</c> cast come from the EF model, so the statement matches whatever the model maps (and the
/// SQLite test model's converters). <c>Seq</c> and <c>TxId</c> are left to the database.
/// </summary>
internal static class WallChangeInsert
{
    public static async Task<long?> ExecuteAsync(BlocwerkDbContext db, WallChange row, CancellationToken cancellationToken)
    {
        if (db.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException(
                "A feed row is appended only inside an open transaction, so it commits or rolls back with the write it describes.");
        }

        var entityType = db.Model.FindEntityType(typeof(WallChange))
            ?? throw new InvalidOperationException("WallChange is not in the model.");
        var table = StoreObjectIdentifier.Table(entityType.GetTableName()!, entityType.GetSchema());
        var isNpgsql = db.Database.IsNpgsql();

        // Real DbParameters: EF maps a bare value by its CLR type, which a null (DBNull) has none of. The command only
        // makes them; EF runs the statement.
        await using var parameterSource = db.Database.GetDbConnection().CreateCommand();
        var columns = new List<string>();
        var placeholders = new List<string>();
        var values = new List<object>();
        foreach (var (name, value) in Values(row))
        {
            var property = entityType.FindProperty(name)!;
            var cast = isNpgsql && property.GetColumnType() == "jsonb" ? "::jsonb" : string.Empty;
            var parameter = parameterSource.CreateParameter();
            parameter.ParameterName = $"feed{values.Count}";
            parameter.Value = ToProvider(property, value) ?? DBNull.Value;
            columns.Add($"\"{property.GetColumnName(table)}\"");
            placeholders.Add($"{{{values.Count}}}{cast}");
            values.Add(parameter);
        }

        var target = table.Schema is null ? $"\"{table.Name}\"" : $"\"{table.Schema}\".\"{table.Name}\"";
        var txId = $"\"{entityType.FindProperty(nameof(WallChange.TxId))!.GetColumnName(table)}\"";
        var sql = $"INSERT INTO {target} ({string.Join(", ", columns)}) VALUES ({string.Join(", ", placeholders)}) RETURNING {txId} AS \"Value\"";

        // Not composed (no Single over it), so EF runs the INSERT as written instead of wrapping it in a subquery.
        var returned = await db.Database.SqlQueryRaw<long?>(sql, values.ToArray()).ToListAsync(cancellationToken);
        return returned.Single();
    }

    private static object? ToProvider(IProperty property, object? value)
    {
        var converter = property.GetTypeMapping().Converter;
        return value is null || converter is null ? value : converter.ConvertToProvider(value);
    }

    private static IEnumerable<(string Name, object? Value)> Values(WallChange row) =>
    [
        (nameof(WallChange.WallId), row.WallId),
        (nameof(WallChange.BoulderId), row.BoulderId),
        (nameof(WallChange.Kind), row.Kind),
        (nameof(WallChange.EntityId), row.EntityId),
        (nameof(WallChange.Audience), row.Audience),
        (nameof(WallChange.AudienceUserId), row.AudienceUserId),
        (nameof(WallChange.ActorUserId), row.ActorUserId),
        (nameof(WallChange.DataJson), row.DataJson),
        (nameof(WallChange.SchemaVersion), row.SchemaVersion),
        (nameof(WallChange.CreatedAt), row.CreatedAt),
    ];
}
