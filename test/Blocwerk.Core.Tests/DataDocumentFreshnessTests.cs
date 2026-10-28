// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Contracts.Documents;
using Blocwerk.Contracts.Feed;
using Blocwerk.Core.Assets.DataDocs;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Feed;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The documents' shas and cursors: the same rows give the same bytes, a document is rebuilt only on a row of its own
/// input kinds after its build mark, and the index answers the oldest cursor, so a cached document plus a later write is
/// replayed from before the write.
/// </summary>
public sealed class DataDocumentFreshnessTests
{
    [Fact]
    public async Task TheSameRows_GiveTheSameBytes_FromAnyStore()
    {
        using var f = await SeededAsync();
        var first = await f.IndexAsync();
        var access = await f.AccessAsync();

        var again = (await f.Service(new DataDocumentStore()).IndexAsync(f.WallId, access.Audience!.Value, default))!;

        Assert.Equal(first.Docs.Select(d => d.Sha), again.Docs.Select(d => d.Sha));
        foreach (var doc in first.Docs)
        {
            var a = f.Store.Find(f.WallId, access.Audience!.Value, doc.Kind, doc.Sha)!;
            Assert.Equal(a.Sha, ContentHashHex(a.Json));
        }
    }

    [Fact]
    public async Task ARowOfNoInputKind_RebuildsNothing()
    {
        using var f = await SeededAsync();
        var first = await f.IndexAsync();
        var held = f.Store.Current(f.WallId, DocumentAudience.Setters, DataDocumentKinds.Boulders);

        await f.AddFeedRowAsync(FeedKinds.VideoStatus);
        var second = await f.IndexAsync();

        Assert.Equal(first.Cursor, second.Cursor);
        Assert.Equal(first.Docs, second.Docs);
        Assert.Same(held, f.Store.Current(f.WallId, DocumentAudience.Setters, DataDocumentKinds.Boulders));
    }

    /// <summary>A rebuild that changes nothing keeps the sha and moves only that document's cursor.</summary>
    [Fact]
    public async Task AnInputRow_ThatChangesNothing_KeepsTheSha_AndMovesOnlyThatCursor()
    {
        using var f = await SeededAsync();
        var first = await f.IndexAsync();

        var row = await f.AddFeedRowAsync(FeedKinds.BoulderStats);
        var second = await f.IndexAsync();

        Assert.Equal(first.Docs.Select(d => d.Sha), second.Docs.Select(d => d.Sha));
        Assert.True(Position(Entry(second, DataDocumentKinds.Boulders).Cursor) >= FeedPosition.Of(row));
        Assert.Equal(Entry(first, DataDocumentKinds.Gallery).Cursor, Entry(second, DataDocumentKinds.Gallery).Cursor);
        Assert.True(Position(second.Cursor) < FeedPosition.Of(row));
    }

    /// <summary>The acceptance check on SQLite (the Postgres one is in <see cref="DataDocumentPostgresTests"/>).</summary>
    [Fact]
    public async Task ACachedDocumentPlusALaterWrite_IsRebuilt_AndTheIndexCursorIsAtOrBeforeTheWrite()
    {
        using var f = await SeededAsync();
        var first = await f.IndexAsync();

        var (boulderId, row) = await f.AddBoulderAsync("Later");
        var second = await f.IndexAsync();

        Assert.NotEqual(DataDocumentFixture.Sha(first, DataDocumentKinds.Boulders), DataDocumentFixture.Sha(second, DataDocumentKinds.Boulders));
        Assert.Contains((await f.DocumentAsync<BouldersDocument>(DataDocumentKinds.Boulders)).Boulders, b => b.Id == boulderId);
        Assert.True(Position(second.Cursor) < FeedPosition.Of(row), "the documents the write did not touch replay it");
        Assert.True(Position(Entry(second, DataDocumentKinds.Boulders).Cursor) >= FeedPosition.Of(row));
    }

    /// <summary>
    /// A share viewer is handed the wall's own mark: a members-only row rebuilds its document (same bytes) but neither shows
    /// up in its cursor nor rebuilds it again on the next read.
    /// </summary>
    [Fact]
    public async Task AnOutsidersCursor_IsTheWallsOwnMark_AndAMembersOnlyRowRebuildsOnce()
    {
        using var f = await SeededAsync();
        f.Caller = null;
        var first = await f.IndexAsync(DataDocumentFixture.ShareToken);

        var row = await f.AddFeedRowAsync(FeedKinds.BoulderChanged, FeedAudience.Members);
        var second = await f.IndexAsync(DataDocumentFixture.ShareToken);
        var held = f.Store.Current(f.WallId, DocumentAudience.Outsider, DataDocumentKinds.Boulders)!;
        var third = await f.IndexAsync(DataDocumentFixture.ShareToken);

        Assert.Equal(DataDocumentFixture.Sha(first, DataDocumentKinds.Boulders), DataDocumentFixture.Sha(second, DataDocumentKinds.Boulders));
        Assert.True(Position(Entry(second, DataDocumentKinds.Boulders).Cursor) < FeedPosition.Of(row));
        Assert.True(held.BuildMark.Position >= FeedPosition.Of(row));
        Assert.Same(held, f.Store.Current(f.WallId, DocumentAudience.Outsider, DataDocumentKinds.Boulders));
        Assert.Equal(Entry(second, DataDocumentKinds.Boulders).Cursor, Entry(third, DataDocumentKinds.Boulders).Cursor);

        f.Caller = f.Member;
        Assert.True(Position(Entry(await f.IndexAsync(), DataDocumentKinds.Boulders).Cursor) >= FeedPosition.Of(row));
    }

    /// <summary>A document whose cursor retention has passed would resync its client forever: it is rebuilt.</summary>
    [Fact]
    public async Task ADocumentBelowTheRetentionFloor_IsRebuiltAboveIt()
    {
        using var f = await SeededAsync();
        await f.IndexAsync();
        var row = await f.AddFeedRowAsync(FeedKinds.CommentAdded);
        await using (var db = f.Harness.CreateContext())
        {
            await db.FeedEpochs.ExecuteUpdateAsync(u => u.SetProperty(e => e.FloorSeq, row.Seq).SetProperty(e => e.FloorTxId, row.TxId));
        }

        var index = await f.IndexAsync();

        Assert.All(index.Docs, d => Assert.True(Position(d.Cursor) >= FeedPosition.Of(row), d.Kind));
    }

    internal static FeedPosition Position(string cursor) => FeedCatchUp.Parse(cursor).Position;

    internal static DataDocumentEntry Entry(DataDocumentIndex index, string kind) => index.Docs.Single(d => d.Kind == kind);

    private static string ContentHashHex(byte[] json) => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(json));

    private static async Task<DataDocumentFixture> SeededAsync()
    {
        var f = new DataDocumentFixture();
        await f.SeedAsync();
        return f;
    }
}
