// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Text.Json;
using Blocwerk.Client.Store;
using Blocwerk.Contracts.Documents;
using Blocwerk.Contracts.Feed;
using Blocwerk.Contracts.Mine;

namespace Blocwerk.Core.Tests.OfflineClientStore;

/// <summary>
/// A wall's documents as the server writes them (the Contracts records through <see cref="DataDocumentSchema.Options"/>),
/// so the tests also pin that the client reads the server's JSON, and the device rows that name them.
/// </summary>
internal static class StoreWorld
{
    public const string UserA = "u-aaaaaaaaaaaaaaaaaaaa";
    public const string UserB = "u-bbbbbbbbbbbbbbbbbbbb";
    public const string Kiosk = "k-kkkkkkkkkkkkkkkkkkkk";
    public const string Anon = "anon";

    public static readonly Guid Wall = Guid.Parse("11111111-1111-1111-1111-111111111111");
    public static readonly Guid Boulder1 = Guid.Parse("b1111111-1111-1111-1111-111111111111");
    public static readonly Guid Boulder2 = Guid.Parse("b2222222-2222-2222-2222-222222222222");
    public static readonly Guid CommentA = Guid.Parse("c1111111-1111-1111-1111-111111111111");
    public static readonly Guid CommentB = Guid.Parse("c2222222-2222-2222-2222-222222222222");
    public static readonly FeedActor Alice = new(Guid.Parse("a1111111-1111-1111-1111-111111111111"), "Alice", false, null);
    public static readonly DateTimeOffset At = new(2026, 10, 23, 10, 0, 0, TimeSpan.Zero);

    public static string Sha(char c) => new(c, 64);

    public static string Json<T>(T document) => JsonSerializer.Serialize(document, DataDocumentSchema.Options);

    public static BouldersDocumentBoulder Boulder(Guid id, string name) => new(
        id, name, "6a", 1, null, false, false, At, At, "Alice", false, false, false, null, new BouldersDocumentStats(0, 0, null), []);

    public static CommentsDocumentComment Comment(Guid id, Guid boulderId, string text) => new(id, boulderId, Alice, text, At);

    public static string WallJson() => Json(new WallDocument(1, Wall, "Alpha", null, 30, 1, null, null, [], []));

    public static string BouldersJson(params BouldersDocumentBoulder[] boulders) => Json(new BouldersDocument(1, Wall, boulders));

    public static string CommentsJson(params CommentsDocumentComment[] comments) => Json(new CommentsDocument(1, Wall, comments));

    /// <summary>A ready device under <paramref name="partition"/> holding the wall with two boulders and two comments.</summary>
    public static FakeStoreSource Device(string partition, Guid? shareWall = null, IReadOnlyList<StoreMineRow>? mine = null)
    {
        var source = new FakeStoreSource();
        Put(source, partition, new()
        {
            [DataDocumentKinds.Wall] = new(Sha('a'), WallJson()),
            [DataDocumentKinds.Boulders] = new(Sha('b'), BouldersJson(Boulder(Boulder1, "One"), Boulder(Boulder2, "Two"))),
            [DataDocumentKinds.Comments] = new(Sha('c'), CommentsJson(Comment(CommentA, Boulder1, "nice"), Comment(CommentB, Boulder2, "hard"))),
        }, shareWall, mine);
        return source;
    }

    public static void Put(FakeStoreSource source, string partition, Dictionary<string, StoreDocText> docs, Guid? shareWall = null, IReadOnlyList<StoreMineRow>? mine = null)
    {
        var refs = docs.ToDictionary(d => d.Key, d => new StoreDocRef(d.Value.Sha, "1.2.3"));
        source.Snapshot = new StoreSnapshot("ready", partition, shareWall, [new StoreWallRow(Wall, refs)], mine ?? []);
        source.Docs[(partition, Wall)] = new StoreDocs(partition, Wall, docs);
    }

    public static StoreMineRow Mine(params MineDocumentTick[] ticks) => new(Wall, "1.2.3", ticks, [Boulder2]);

    public static FeedEnvelope Envelope(string kind, Guid? entity = null, Guid? boulder = null, object? data = null, FeedContent? hydrated = null) => new()
    {
        Cursor = "1.2.4",
        WallId = Wall,
        Kind = kind,
        EntityId = entity,
        BoulderId = boulder,
        At = At,
        Actor = Alice,
        Data = data is null ? null : JsonSerializer.SerializeToElement(data, FeedJson.Options),
        Hydrated = hydrated,
    };

    public static async Task<(ClientStore Store, CachedWall Wall)> LoadedAsync(FakeStoreSource source)
    {
        var store = new ClientStore(source);
        await store.StartAsync();
        var wall = await store.GetWallAsync(Wall);
        Assert.NotNull(wall);
        return (store, wall!);
    }
}
