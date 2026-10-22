// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Data.Common;
using Blocwerk.Core.Entities;

namespace Blocwerk.Core.Feed;

/// <summary>Rows written inside an explicit transaction, held until THAT transaction commits, then observed and woken.</summary>
internal sealed class FeedPendingCommit
{
    public FeedPendingCommit(DbTransaction transaction)
    {
        Transaction = transaction;
    }

    /// <summary>Gets the transaction the saves ran in; a commit of any other transaction never releases these rows.</summary>
    public DbTransaction Transaction { get; }

    public List<WallChange> Rows { get; } = [];
}
