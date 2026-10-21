// <copyright file="DatabaseErrors.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Blocwerk.Core.Data;

/// <summary>
/// Classifies a failed save, so a race on a unique index can be resolved instead of failing, and nothing else is ever
/// mistaken for one. The same rule as the account linker's <c>PostgresErrors</c>, plus the SQLite form the tests run on.
/// </summary>
public static class DatabaseErrors
{
    /// <summary>SQLite's extended result code for a UNIQUE constraint (SQLITE_CONSTRAINT_UNIQUE).</summary>
    private const int SqliteConstraintUnique = 2067;

    /// <summary>
    /// True when the save failed on a unique index: Postgres SQLSTATE 23505, or SQLite's extended code for a UNIQUE
    /// constraint. Core does not reference the SQLite provider (only the tests run on it), so that code is read from the
    /// provider's exception by its documented property name; a NOT NULL, CHECK or foreign-key failure is never one.
    /// </summary>
    /// <param name="exception">The failed save.</param>
    /// <returns>Whether it was a unique violation.</returns>
    public static bool IsUniqueViolation(DbUpdateException exception) => exception.InnerException switch
    {
        PostgresException pg => pg.SqlState == PostgresErrorCodes.UniqueViolation,
        DbException db => db.GetType().GetProperty("SqliteExtendedErrorCode")?.GetValue(db) is SqliteConstraintUnique,
        _ => false,
    };
}
