// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.Tests;

/// <summary>A <c>[Theory]</c> that needs the Postgres server of <see cref="PostgresTestDatabase"/> and is skipped without it.</summary>
public sealed class PostgresTheoryAttribute : TheoryAttribute
{
    public PostgresTheoryAttribute()
    {
        Skip = PostgresTestDatabase.SkipReason;
    }
}
