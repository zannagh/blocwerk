// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Blocwerk.Core.Assets.Backfill;

/// <summary>
/// The backfill's statements for one table, with the identifiers taken from the EF model (never from input). Placeholders
/// are EF's <c>{0}</c> parameters. Every read walks the table by id (<c>"Id" &gt; {0} ORDER BY "Id"</c>), so a pass never
/// revisits a row it skipped, and every write keeps <c>hash IS NULL</c> in its guard.
/// </summary>
/// <remarks>
/// The row version token of a guarded write is <c>xmin</c> on Postgres: the id of the transaction that wrote the row
/// version, which any update moves. SQLite (tests only) has none, so the token there is the guarded column itself.
/// </remarks>
internal sealed class ContentHashBackfillSql
{
    private readonly IEntityType entity;
    private readonly string table;
    private readonly string id;

    private ContentHashBackfillSql(IEntityType entity, bool postgres)
    {
        this.entity = entity;
        Postgres = postgres;
        table = Quote(entity.GetTableName()!);
        id = Column("Id");
    }

    public bool Postgres { get; }

    public static ContentHashBackfillSql For(BlocwerkDbContext db, Type entity) =>
        new(db.Model.FindEntityType(entity) ?? throw new InvalidOperationException($"{entity.Name} is not in the model."), db.Database.IsNpgsql());

    /// <summary>Postgres: hashes a batch of blobs in place, skipping rows a writer holds; returns the stamped rows by id.</summary>
    public string StampBlobs(ContentHashColumn column)
    {
        var (bytes, hash) = (Column(column.Bytes), Column(column.Hash));
        return $$"""
            WITH batch AS (
                SELECT {{id}} FROM {{table}}
                WHERE {{hash}} IS NULL AND {{bytes}} IS NOT NULL AND {{id}} > {0}
                ORDER BY {{id}} LIMIT {1}
                FOR NO KEY UPDATE SKIP LOCKED),
            stamped AS (
                UPDATE {{table}} t SET {{hash}} = sha256(t.{{bytes}}) FROM batch WHERE t.{{id}} = batch.{{id}}
                RETURNING t.{{id}} AS "Id", octet_length(t.{{bytes}})::bigint AS "Size")
            SELECT "Id", "Size" FROM stamped ORDER BY "Id"
            """;
    }

    /// <summary>SQLite: a batch of unhashed blobs, to hash in the app.</summary>
    public string SelectBlobs(ContentHashColumn column) =>
        $$"""
        SELECT {{id}} AS "Id", {{Column(column.Bytes)}} AS "Bytes" FROM {{table}}
        WHERE {{Column(column.Hash)}} IS NULL AND {{Column(column.Bytes)}} IS NOT NULL AND {{id}} > {0}
        ORDER BY {{id}} LIMIT {1}
        """;

    /// <summary>SQLite: stamps one blob's hash, only while the row still holds the bytes that were hashed.</summary>
    public string StampBlob(ContentHashColumn column) =>
        $"UPDATE {table} SET {Column(column.Hash)} = {{0}} WHERE {id} = {{1}} AND {Column(column.Hash)} IS NULL AND {Column(column.Bytes)} = {{2}}";

    /// <summary>The rows of a blob column still without a hash.</summary>
    public string CountPending(ContentHashColumn column) =>
        $$"""SELECT CAST(count(*) AS integer) AS "Value" FROM {{table}} WHERE {{Column(column.Hash)}} IS NULL AND {{Column(column.Bytes)}} IS NOT NULL""";

    /// <summary>A batch of file rows without a hash: id, stored name, row version token.</summary>
    public string SelectFiles(ContentHashFileColumn column)
    {
        var path = Column(column.Path);
        return $$"""
            SELECT {{id}} AS "Id", {{path}} AS "Value", {{Token(path)}} AS "Token" FROM {{table}}
            WHERE {{Column(column.Hash)}} IS NULL AND {{path}} IS NOT NULL AND {{id}} > {0}
            ORDER BY {{id}} LIMIT {1}
            """;
    }

    /// <summary>Stamps one file's hash, only while the row is the version that was read before hashing.</summary>
    public string StampFile(ContentHashFileColumn column) =>
        $"UPDATE {table} SET {Column(column.Hash)} = {{0}} WHERE {id} = {{1}} AND {Column(column.Hash)} IS NULL AND {Token(Column(column.Path))} = {{2}}";

    /// <summary>A batch of rows with a JSON column: id, JSON, row version token.</summary>
    public string SelectJson(string property)
    {
        var json = Column(property);
        return $$"""
            SELECT {{id}} AS "Id", {{json}} AS "Value", {{Token(json)}} AS "Token" FROM {{table}}
            WHERE {{json}} IS NOT NULL AND {{id}} > {0}
            ORDER BY {{id}} LIMIT {1}
            """;
    }

    /// <summary>Replaces a JSON column, only while the row is the version that was read.</summary>
    public string StampJson(string property) =>
        $"UPDATE {table} SET {Column(property)} = {{0}} WHERE {id} = {{1}} AND {Token(Column(property))} = {{2}}";

    private static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    private string Token(string guardedColumn) => Postgres ? "xmin::text" : guardedColumn;

    private string Column(string property) =>
        Quote((entity.FindProperty(property) ?? throw new InvalidOperationException($"{entity.DisplayName()}.{property} is not mapped.")).GetColumnName());
}
