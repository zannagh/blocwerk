// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Contracts.Feed;
using Blocwerk.Contracts.Feed.Payloads;
using Blocwerk.Core.Entities;
using Blocwerk.Web.State.Live;

namespace Blocwerk.Core.Tests;

/// <summary>
/// A live comment list on Postgres, where cursors carry real transaction ids: a hydrated comment patches in, and a comment
/// the list's load already reflects is skipped when its wake arrives late.
/// </summary>
[Collection(PostgresFeedCollection.Name)]
public sealed class PostgresLeafFeedTests
{
    [PostgresFact]
    public async Task AHydratedComment_PatchesIn_AndOneTheLoadReflects_IsSkipped()
    {
        await using var f = new CircuitCacheFixture(PostgresTestDatabase.Create());
        await f.StartAsync();
        var boulder = await f.AddBoulderAsync("First", f.Holds[0]);
        var batches = new List<LeafBatch>();
        var list = new CommentList();
        using var feed = LeafFeed.Open(f.Cache, new LeafScope(f.WallId, boulder.Id, CommentList.Kinds), run => run(), b =>
        {
            lock (batches)
            {
                batches.Add(b);
            }

            list.Apply(b);
            return Task.CompletedTask;
        });

        var early = await CommentRowAsync(f, boulder, "seen by the load");
        await f.CommitAsync(false, early);
        await feed.LoadAsync(() => Task.FromResult(0));
        f.Signal.Wake(new Core.Feed.FeedWake(f.WallId, early.TxId));

        var later = await CommentRowAsync(f, boulder, "live");
        await f.CommitAsync(true, later);
        await CircuitCacheFixture.WaitUntilAsync(() => list.Rows.Count > 0, seconds: 20);

        Assert.Equal(["live"], list.Rows.Select(r => r.Text));
        lock (batches)
        {
            Assert.DoesNotContain(batches.SelectMany(b => b.Events), e => e.EntityId == early.EntityId);
        }
    }

    private static async Task<WallChange> CommentRowAsync(CircuitCacheFixture f, Boulder boulder, string text)
    {
        await using var db = f.Harness.CreateContext();
        var comment = new BoulderComment { BoulderId = boulder.Id, UserId = f.Harness.Owner.Id, Text = text };
        db.BoulderComments.Add(comment);
        await db.SaveChangesAsync();
        var row = f.Row(FeedKinds.CommentAdded, comment.Id, new CommentPayload(boulder.Id));
        row.BoulderId = boulder.Id;
        row.ActorUserId = f.Harness.Owner.Id;
        return row;
    }
}
