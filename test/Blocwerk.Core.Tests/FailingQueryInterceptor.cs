// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Blocwerk.Core.Tests;

/// <summary>
/// Breaks every SELECT whose text contains <see cref="Marker"/> while <see cref="On"/> is set, by pointing it at a table
/// that does not exist: a lookup that fails on the server (on Postgres it aborts an open transaction, 25P02).
/// </summary>
internal sealed class FailingQueryInterceptor : DbCommandInterceptor
{
    public FailingQueryInterceptor(string marker)
    {
        Marker = marker;
    }

    public string Marker { get; }

    public bool On { get; set; } = true;

    public int Broken { get; private set; }

    public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    {
        Break(command);
        return result;
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    {
        Break(command);
        return ValueTask.FromResult(result);
    }

    private void Break(DbCommand command)
    {
        if (On && command.CommandText.Contains(Marker, StringComparison.Ordinal) && command.CommandText.Contains("SELECT", StringComparison.Ordinal))
        {
            command.CommandText = "SELECT * FROM bw_no_such_table";
            Broken++;
        }
    }
}
