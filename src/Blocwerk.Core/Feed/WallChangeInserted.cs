// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.Feed;

/// <summary>What <see cref="WallChangeInsert"/> reads back from its <c>RETURNING</c> clause.</summary>
internal sealed class WallChangeInserted
{
    public long Seq { get; set; }

    public long? TxId { get; set; }
}
