// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Data;
using Blocwerk.Core.Feed;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Tests;

/// <summary>A context factory over the harness's database whose contexts carry the production feed interceptor.</summary>
internal sealed class FeedDbContextFactory(WallTestHarness harness, IFeedSignal signal) : IDbContextFactory<BlocwerkDbContext>
{
    public BlocwerkDbContext CreateDbContext() =>
        TestDb.Create(harness.DbContextFactory.ConnectionString, StructureFeedKit.Interceptor(signal));
}
