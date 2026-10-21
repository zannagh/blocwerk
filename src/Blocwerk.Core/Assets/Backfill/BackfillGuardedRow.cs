// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.Assets.Backfill;

/// <summary>A row read before slow work (hashing its file): id, the value read, and its row version token (a raw SQL result row).</summary>
internal sealed class BackfillGuardedRow
{
    public Guid Id { get; set; }

    public string Value { get; set; } = string.Empty;

    /// <summary>Gets or sets <c>xmin</c> on Postgres; the guarded column itself on SQLite.</summary>
    public string Token { get; set; } = string.Empty;
}
