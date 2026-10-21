// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.Assets.Backfill;

/// <summary>An unhashed blob read on SQLite, hashed in the app (a raw SQL result row).</summary>
internal sealed class BackfillBlobRow
{
    public Guid Id { get; set; }

    public byte[] Bytes { get; set; } = [];
}
