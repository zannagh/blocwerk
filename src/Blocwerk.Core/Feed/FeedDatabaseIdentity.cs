// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Blocwerk.Core.Feed;

/// <summary>
/// Which physical database the feed lives in: the database's oid and, when the role may read it, the cluster's
/// <c>system_identifier</c>. A <c>pg_restore</c> into a newly created database gets a new oid and a new cluster a new
/// system identifier, so any restore or copy shows up as a mismatch with the identity stored in <see cref="FeedEpoch"/>.
/// </summary>
/// <param name="DatabaseOid">The current database's oid.</param>
/// <param name="SystemIdentifier">The cluster's system identifier, or null when <c>pg_control_system()</c> is not allowed.</param>
public sealed record FeedDatabaseIdentity(long DatabaseOid, long? SystemIdentifier)
{
    private const string OidSql = "SELECT oid::bigint AS \"Value\" FROM pg_database WHERE datname = current_database()";
    private const string MayReadControlSql = "SELECT has_function_privilege('pg_control_system()', 'EXECUTE') AS \"Value\"";
    private const string SystemIdentifierSql = "SELECT system_identifier AS \"Value\" FROM pg_control_system()";

    /// <summary>Reads the identity. Call it outside a transaction: a refused <c>pg_control_system()</c> would abort one.</summary>
    public static async Task<FeedDatabaseIdentity> ReadAsync(BlocwerkDbContext db, ILogger logger, CancellationToken cancellationToken)
    {
        var oid = await db.Database.SqlQueryRaw<long>(OidSql).SingleAsync(cancellationToken);
        return new FeedDatabaseIdentity(oid, await TryReadSystemIdentifierAsync(db, logger, cancellationToken));
    }

    /// <summary>
    /// Whether <paramref name="epoch"/> was started in this database. The oid must match; the system identifier only when
    /// both sides know it. An epoch with no stored oid (never recorded) does not match.
    /// </summary>
    public bool Matches(FeedEpoch epoch) =>
        epoch.DatabaseOid == DatabaseOid
        && (epoch.SystemIdentifier is null || SystemIdentifier is null || epoch.SystemIdentifier == SystemIdentifier);

    private static async Task<long?> TryReadSystemIdentifierAsync(BlocwerkDbContext db, ILogger logger, CancellationToken cancellationToken)
    {
        try
        {
            if (!await db.Database.SqlQueryRaw<bool>(MayReadControlSql).SingleAsync(cancellationToken))
            {
                return null;
            }

            return await db.Database.SqlQueryRaw<long>(SystemIdentifierSql).SingleAsync(cancellationToken);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.InsufficientPrivilege)
        {
            // The database oid alone still tells a restore into a new database apart.
            logger.LogInformation("The feed cannot read pg_control_system(); its restore check uses the database oid alone.");
            return null;
        }
    }
}
