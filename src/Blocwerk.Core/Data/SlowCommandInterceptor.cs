// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Data;

/// <summary>
/// Logs a database command that took longer than <see cref="Threshold"/> at Warning: duration and the SQL text
/// (truncated, parameter values never). EF's own command log stays at Warning level so ordinary commands cost nothing.
/// </summary>
public sealed class SlowCommandInterceptor : DbCommandInterceptor
{
    public static readonly TimeSpan Threshold = TimeSpan.FromMilliseconds(500);

    private const int MaxSql = 400;

    private readonly ILogger<SlowCommandInterceptor> logger;

    public SlowCommandInterceptor(ILogger<SlowCommandInterceptor> logger)
    {
        this.logger = logger;
    }

    public override DbDataReader ReaderExecuted(DbCommand command, CommandExecutedEventData eventData, DbDataReader result)
    {
        Report(command, eventData);
        return result;
    }

    public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
    {
        Report(command, eventData);
        return new ValueTask<DbDataReader>(result);
    }

    public override int NonQueryExecuted(DbCommand command, CommandExecutedEventData eventData, int result)
    {
        Report(command, eventData);
        return result;
    }

    public override ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        Report(command, eventData);
        return new ValueTask<int>(result);
    }

    public override object? ScalarExecuted(DbCommand command, CommandExecutedEventData eventData, object? result)
    {
        Report(command, eventData);
        return result;
    }

    public override ValueTask<object?> ScalarExecutedAsync(DbCommand command, CommandExecutedEventData eventData, object? result, CancellationToken cancellationToken = default)
    {
        Report(command, eventData);
        return new ValueTask<object?>(result);
    }

    internal static string Shorten(string sql)
    {
        var flat = string.Join(' ', sql.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return flat.Length <= MaxSql ? flat : flat[..MaxSql] + "...";
    }

    private void Report(DbCommand command, CommandExecutedEventData eventData)
    {
        if (eventData.Duration >= Threshold)
        {
            logger.LogWarning(
                "Slow database command: {ElapsedMs} ms: {Sql}",
                (long)eventData.Duration.TotalMilliseconds,
                Shorten(command.CommandText));
        }
    }
}
