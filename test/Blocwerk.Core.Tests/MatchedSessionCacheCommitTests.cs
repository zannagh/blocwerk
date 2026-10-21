// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Entities;
using Blocwerk.Core.Refresh;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Tests;

/// <summary>
/// <see cref="MatchedSessionCache"/> on the in-process commits (PR 1.8, replacing its DomainChange bump): every save that
/// changes a matcher input bumps the wall synchronously, a staged-photo-only panel save included (<c>panel.staged</c>),
/// which wrote no feed row and woke nothing before.
/// </summary>
public sealed class MatchedSessionCacheCommitTests : IDisposable
{
    private readonly WallTestHarness harness = new();
    private readonly RecordingFeedCommits commits = new();

    public void Dispose() => harness.Dispose();

    [Fact]
    public async Task AStagedPhotoOnlySave_EndsTheReuse_AtTheCommit()
    {
        await harness.SeedWallAsync(holdCount: 1);
        var panel = new WallPanel { WallId = harness.WallId, StagedPhoto = [1, 2] };
        await using (var seed = harness.CreateContext())
        {
            seed.WallPanels.Add(panel);
            await seed.SaveChangesAsync();
        }

        var (cache, _, db) = await WarmAsync();
        await using (db)
        {
            await using (var edit = Intercepted())
            {
                (await edit.WallPanels.SingleAsync(p => p.Id == panel.Id)).StagedPhoto = [3, 4, 5];
                await edit.SaveChangesAsync();
            }

            Assert.Null(await cache.PeekAsync(db, harness.WallId));
        }
    }

    [Fact]
    public async Task ACommitOfAnotherWall_KeepsTheReuse()
    {
        await harness.SeedWallAsync(holdCount: 1);
        var (cache, session, db) = await WarmAsync();
        await using (db)
        {
            await using (var edit = Intercepted())
            {
                var other = new Wall { Name = "Other", OwnerId = harness.Owner.Id };
                edit.Walls.Add(other);
                edit.Holds.Add(new Hold { WallId = other.Id, X = 0.5, Y = 0.5, Radius = 0.02 });
                await edit.SaveChangesAsync();
            }

            Assert.Same(session, await cache.PeekAsync(db, harness.WallId));
        }
    }

    private async Task<(MatchedSessionCache Cache, BigUpdateSession Session, Data.BlocwerkDbContext Db)> WarmAsync()
    {
        var cache = new MatchedSessionCache(commits.Commits);
        var session = new BigUpdateSession(harness.WallId, Guid.NewGuid(), [], [], [], []);
        var db = harness.CreateContext();
        await cache.GetOrMatchAsync(db, harness.WallId, false, () => Task.FromResult(session));
        Assert.Same(session, await cache.PeekAsync(db, harness.WallId));
        return (cache, session, db);
    }

    private Data.BlocwerkDbContext Intercepted() =>
        TestDb.Create(harness.DbContextFactory.ConnectionString, FeedCommitsAfterCommitTests.Interceptor(commits));
}
