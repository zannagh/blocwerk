// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Contracts.Feed;
using Blocwerk.Core.Assets.DataDocs;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Feed;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The reader's own marks on Postgres (PR 3.4): the queries translate on the real provider, read only the caller's rows
/// of this wall's class, and the mark is read before them, with real transaction ids.
/// </summary>
public sealed class MineDocumentPostgresTests
{
    [PostgresFact]
    [Trait("Db", "Postgres")]
    public async Task TheMarks_OnPostgres_AreTheCallersOwn_ForTheClass()
    {
        using var f = await SeededAsync();

        var member = await f.MineAsync();
        Assert.Equal(new[] { f.Docs.PublishedId, f.Docs.DraftId, f.Docs.ArchivedId, f.Docs.HistoricId }.Order(), member.Ticks.Select(t => t.BoulderId));
        Assert.Equal(2, member.Ticks.Single(t => t.BoulderId == f.Docs.PublishedId).Attempts);
        Assert.Equal(new[] { f.Docs.PublishedId, f.Docs.DraftId }.Order(), member.Favourites);

        var outsider = await f.Service().BuildAsync(f.WallId, f.Member.Id, DocumentAudience.Outsider, default);
        Assert.Equal(new[] { f.Docs.PublishedId, f.Docs.HistoricId }.Order(), outsider.Ticks.Select(t => t.BoulderId));

        f.Docs.BindKiosk(f.WallId);
        Assert.Equal(FeedAccessRefusal.NoAccess, (await f.AccessAsync()).Refusal);
    }

    /// <summary>A tick that commits after the mark was read is in the document, and its feed row is after the cursor.</summary>
    [PostgresFact]
    [Trait("Db", "Postgres")]
    public async Task ATickBetweenTheMarkAndTheRead_IsInTheDocument_AndAfterItsCursor()
    {
        using var f = await SeededAsync();
        var feed = new WriteAfterMarkFeed(f.Docs.Feed);
        f.Docs.Feed = feed;
        WallChange? row = null;
        feed.Arm(async () =>
        {
            // The tick and its own-row in one save, as the interceptor writes them.
            await using var db = f.Docs.Harness.CreateContext();
            var attempt = new Attempt { BoulderId = f.Docs.HistoricId, UserId = f.Setter.Id, Type = AttemptType.Flash };
            row = new WallChange
            {
                WallId = f.WallId, BoulderId = attempt.BoulderId, Kind = FeedKinds.TickMine, EntityId = attempt.Id,
                Audience = FeedAudience.User, AudienceUserId = f.Setter.Id, SchemaVersion = FeedSchema.PayloadVersion,
            };
            db.Attempts.Add(attempt);
            db.WallChanges.Add(row);
            await db.SaveChangesAsync();
        });
        f.Docs.Caller = f.Setter;

        var mine = await f.MineAsync();

        var written = Assert.IsType<WallChange>(row);
        Assert.NotNull(written.TxId);
        Assert.Contains(mine.Ticks, t => t.BoulderId == f.Docs.HistoricId && t.Flashed);
        Assert.True(FeedCatchUp.Parse(mine.Cursor).Position < FeedPosition.Of(written), "the tick's row is replayed after the cursor");
    }

    private static async Task<MineDocumentFixture> SeededAsync()
    {
        var f = new MineDocumentFixture(PostgresTestDatabase.Create());
        await f.SeedAsync();
        return f;
    }
}
