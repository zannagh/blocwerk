// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Data.Common;

namespace Blocwerk.Core.Feed;

/// <summary>Wakes of saves inside an explicit transaction, held until THAT transaction commits.</summary>
internal sealed class FeedPendingCommit
{
    public FeedPendingCommit(DbTransaction transaction)
    {
        Transaction = transaction;
    }

    /// <summary>Gets the transaction the saves ran in; a commit of any other transaction never releases these wakes.</summary>
    public DbTransaction Transaction { get; }

    public List<FeedWake> Wakes { get; } = [];
}
