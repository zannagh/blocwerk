// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Contracts.Documents;
using Blocwerk.Contracts.Feed;
using Blocwerk.Core.Assets.DataDocs;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Feed;
using static Blocwerk.Core.Tests.DataDocumentFreshnessTests;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The data documents on Postgres, with real transaction ids: the class matrix, and the build cursor against a write
/// that commits between the cursor read and the build, or is still in flight while the build reads.
/// </summary>
public sealed class DataDocumentPostgresTests
{
    [PostgresFact]
    [Trait("Db", "Postgres")]
    public async Task TheClasses_OnPostgres_SettersSeeTheDraft_TheKioskActingAsASetterDoesNot()
    {
        using var f = await SeededAsync();
        f.Caller = f.Setter;
        Assert.Contains((await f.DocumentAsync<BouldersDocument>(DataDocumentKinds.Boulders)).Boulders, b => b.Id == f.DraftId);
        var settersSha = DataDocumentFixture.Sha(await f.IndexAsync(), DataDocumentKinds.Boulders);

        f.BindKiosk(f.WallId);
        Assert.Equal(DocumentAudience.Kiosk, (await f.AccessAsync()).Audience);
        var kiosk = await f.DocumentAsync<BouldersDocument>(DataDocumentKinds.Boulders);
        Assert.Equal(f.EveryonesBoulders, kiosk.Boulders.Select(b => b.Id));
        var published = kiosk.Boulders.Single(b => b.Id == f.PublishedId);
        Assert.Equal(1, published.Stats.Ascents);
        Assert.Equal(4, published.Stats.RatingAverage);
        Assert.Equal(404, (await f.DocumentHttpAsync(DataDocumentKinds.Boulders, settersSha)).Response.StatusCode);
    }

    /// <summary>The write commits after the mark was read and before the build: the build has it, the cursor is before it.</summary>
    [PostgresFact]
    [Trait("Db", "Postgres")]
    public async Task AWriteBetweenTheCursorReadAndTheBuild_IsInTheDocument_AndAfterItsCursor()
    {
        using var f = await SeededAsync();
        var feed = new WriteAfterMarkFeed(f.Feed);
        f.Feed = feed;
        await f.IndexAsync();
        await f.AddFeedRowAsync(FeedKinds.BoulderStats);
        (Guid Id, WallChange Row)? written = null;
        feed.Arm(async () => written = await f.AddBoulderAsync("Raced"));

        var raced = await f.IndexAsync();

        var write = Assert.NotNull(written);
        Assert.NotNull(write.Row.TxId);
        Assert.Contains((await f.DocumentAsync<BouldersDocument>(DataDocumentKinds.Boulders)).Boulders, b => b.Id == write.Id);
        Assert.True(Position(Entry(raced, DataDocumentKinds.Boulders).Cursor) < FeedPosition.Of(write.Row));
        Assert.True(Position(raced.Cursor) < FeedPosition.Of(write.Row));

        // The horizon is cluster-wide: another test database's open transaction can hold the mark below the write for a
        // while, and until it passes, every index rightly rebuilds the document again (same bytes, a cursor still before).
        var next = await f.IndexAsync();
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (Position(Entry(next, DataDocumentKinds.Boulders).Cursor) < FeedPosition.Of(write.Row) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100);
            next = await f.IndexAsync();
        }

        Assert.Equal(DataDocumentFixture.Sha(raced, DataDocumentKinds.Boulders), DataDocumentFixture.Sha(next, DataDocumentKinds.Boulders));
        Assert.True(Position(Entry(next, DataDocumentKinds.Boulders).Cursor) >= FeedPosition.Of(write.Row));
    }

    /// <summary>The write is still open while the build reads: the document misses it, and the next index rebuilds once it commits.</summary>
    [PostgresFact]
    [Trait("Db", "Postgres")]
    public async Task AWriteInFlightDuringTheBuild_IsMissed_ThenRebuiltOnceItCommits()
    {
        using var f = await SeededAsync();
        await f.IndexAsync();
        await f.AddFeedRowAsync(FeedKinds.BoulderStats);
        await using var writer = f.Harness.CreateContext();
        await using var transaction = await writer.Database.BeginTransactionAsync();
        var (boulderId, row) = await f.AddBoulderAsync("In flight", writer);

        var during = await f.IndexAsync();
        Assert.DoesNotContain((await f.DocumentAsync<BouldersDocument>(DataDocumentKinds.Boulders)).Boulders, b => b.Id == boulderId);
        await transaction.CommitAsync();
        var after = await f.IndexAsync();

        Assert.True(Position(during.Cursor) < FeedPosition.Of(row));
        Assert.NotEqual(DataDocumentFixture.Sha(during, DataDocumentKinds.Boulders), DataDocumentFixture.Sha(after, DataDocumentKinds.Boulders));
        Assert.Contains((await f.DocumentAsync<BouldersDocument>(DataDocumentKinds.Boulders)).Boulders, b => b.Id == boulderId);
        Assert.True(Position(after.Cursor) < FeedPosition.Of(row), "the documents the write did not touch replay it");
    }

    private static async Task<DataDocumentFixture> SeededAsync()
    {
        var f = new DataDocumentFixture(PostgresTestDatabase.Create());
        await f.SeedAsync();
        return f;
    }
}
