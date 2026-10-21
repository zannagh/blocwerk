// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Data;
using Blocwerk.Core.Feed;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Tests;

/// <summary>The session-less factory a background worker takes, with the production feed interceptor on its contexts.</summary>
internal sealed class FeedRootDbContextFactory(WallTestHarness harness, IFeedSignal signal)
    : RootDbContextFactory(new DbContextOptionsBuilder<BlocwerkDbContext>().Options)
{
    public override BlocwerkDbContext CreateDbContext() =>
        TestDb.Create(harness.DbContextFactory.ConnectionString, StructureFeedKit.Interceptor(signal));
}
