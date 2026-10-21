// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.Assets.Backfill;

/// <summary>What a backfill pass did to one column, or to all of them.</summary>
/// <param name="Stamped">Rows that got their hash.</param>
/// <param name="Bytes">Bytes hashed for them.</param>
/// <param name="Retry">Rows left for another pass: locked by a writer, or changed while their file was hashed.</param>
/// <param name="Missing">File rows whose file is not there (never retried within a start; the next start looks again).</param>
public sealed record BackfillCount(int Stamped, long Bytes, int Retry, int Missing)
{
    public static BackfillCount None { get; } = new(0, 0, 0, 0);

    public static BackfillCount operator +(BackfillCount a, BackfillCount b) =>
        new(a.Stamped + b.Stamped, a.Bytes + b.Bytes, a.Retry + b.Retry, a.Missing + b.Missing);
}
