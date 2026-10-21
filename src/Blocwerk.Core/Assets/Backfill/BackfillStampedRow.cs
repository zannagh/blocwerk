// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.Assets.Backfill;

/// <summary>A blob row the Postgres batch hashed (a raw SQL result row).</summary>
internal sealed class BackfillStampedRow
{
    public Guid Id { get; set; }

    public long Size { get; set; }
}
