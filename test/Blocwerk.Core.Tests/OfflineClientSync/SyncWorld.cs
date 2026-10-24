// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Text.Json;
using Blocwerk.Client.Store;
using Blocwerk.Client.Sync;
using Blocwerk.Contracts.Documents;
using Blocwerk.Contracts.Feed;
using Blocwerk.Contracts.Mine;
using Blocwerk.Core.Tests.OfflineClientStore;
using static Blocwerk.Core.Tests.OfflineClientStore.StoreWorld;

namespace Blocwerk.Core.Tests.OfflineClientSync;

/// <summary>
/// A device (<see cref="StoreWorld.Device"/>: the wall, its boulders and comments, all built at cursor <c>1.2.3</c>)
/// with a fake server and clock, and a <see cref="ClientSync"/> started on the wall's /v/ page.
/// </summary>
internal sealed class SyncWorld
{
    public const string Built = "1.2.3";

    private SyncWorld(FakeStoreSource source, ClientStore store, FakeSyncTransport? transport = null)
    {
        Source = source;
        Store = store;
        Transport = transport ?? new FakeSyncTransport();
        Transport.MePartition = source.Snapshot.Partition;
        Transport.IndexDocs = source.Snapshot.Walls.Single().Data.ToDictionary(d => d.Key, d => d.Value);
        Sync = new ClientSync(store, Transport, Clock);
    }

    public static string WallPage => $"https://blocwerk.test/v/walls/{Wall}";

    public FakeStoreSource Source { get; }

    public ClientStore Store { get; }

    public FakeSyncTransport Transport { get; }

    public FakeSyncClock Clock { get; } = new();

    public ClientSync Sync { get; }

    /// <summary>A device of <paramref name="partition"/>, the store started, the sync not yet.</summary>
    public static async Task<SyncWorld> DeviceAsync(string partition, IReadOnlyList<StoreMineRow>? mine = null, FakeSyncTransport? transport = null)
    {
        var source = Device(partition, shareWall: partition == Anon ? Wall : null, mine: mine);
        var store = new ClientStore(source);
        await store.StartAsync();
        return new SyncWorld(source, store, transport);
    }

    /// <summary>A device of <paramref name="partition"/> syncing the wall's page.</summary>
    public static async Task<SyncWorld> SyncingAsync(string partition = UserA, string? page = null, IReadOnlyList<StoreMineRow>? mine = null, FakeSyncTransport? transport = null)
    {
        var world = await DeviceAsync(partition, mine, transport);
        await world.Sync.StartAsync(page ?? WallPage);
        await world.Sync.Idle;
        return world;
    }

    public static FeedEnvelope At(string cursor, string kind, Guid? entity = null, Guid? boulder = null, string? text = null) =>
        Envelope(kind, entity, boulder, hydrated: text is null ? null : new FeedContent(text, StoreWorld.At)) with { Cursor = cursor };

    public static SyncResponse Mine(string partition, params Guid[] favourites) =>
        new(200, Body: JsonSerializer.Serialize(new MineDocument(1, Wall, partition, Built, [], favourites), DataDocumentSchema.Options));

    public async Task<CachedWall> WallAsync() => (await Store.GetWallAsync(Wall))!;

    public async Task<IReadOnlyList<string>> CommentTextsAsync(Guid boulder) =>
        (await WallAsync()).CommentsFor(boulder).Select(c => c.Text).ToList();

    /// <summary>Lets the loop take in what was pushed.</summary>
    public Task Settled() => Sync.Idle;
}
