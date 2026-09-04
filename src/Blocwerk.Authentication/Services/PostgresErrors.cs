using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Blocwerk.Authentication.Services;

/// <summary>
/// Classifies database exceptions raised while persisting identity rows, so a genuine concurrency race
/// on a unique index can be resolved gracefully instead of surfacing as an HTTP 500.
/// </summary>
internal static class PostgresErrors
{
    /// <summary>
    /// True when the exception wraps a Postgres unique-violation (SQLSTATE 23505) — the error raised
    /// when a concurrent insert already wrote a row with the same unique key. Detection is deliberately
    /// specific to this SQLSTATE so unrelated <see cref="DbUpdateException"/>s are never swallowed.
    /// </summary>
    internal static bool IsUniqueViolation(DbUpdateException exception) =>
        exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };
}
