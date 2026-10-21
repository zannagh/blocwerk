// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Feed;

/// <summary>Picks the horizon for a context's provider.</summary>
public static class FeedHorizons
{
    /// <summary>The Postgres horizon for an Npgsql context, the SQLite one otherwise.</summary>
    public static IFeedHorizon For(BlocwerkDbContext db) =>
        db.Database.IsNpgsql() ? PostgresFeedHorizon.Instance : SqliteFeedHorizon.Instance;
}
