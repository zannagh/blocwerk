// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Blocwerk.Core.Tests;

/// <summary>
/// Sends every raw insert of a feed row to a table that does not exist, so the database itself fails it (and Postgres
/// aborts the transaction unless a savepoint contains the failure). Every other command runs as written.
/// </summary>
internal sealed class FailingFeedInsert : DbCommandInterceptor
{
    private const string Insert = "INSERT INTO \"WallChanges\"";

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    {
        command.CommandText = command.CommandText.Replace(Insert, "INSERT INTO \"WallChangesGone\"", StringComparison.Ordinal);
        return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
    }
}
