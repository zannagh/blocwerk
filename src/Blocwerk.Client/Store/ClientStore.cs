// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Text.Json;
using Blocwerk.Contracts.Documents;
using Blocwerk.Contracts.Feed;
using Blocwerk.Contracts.Mine;

namespace Blocwerk.Client.Store;

/// <summary>
/// The offline client's data (Phase 3 plan §1, PR 3.10): what the device cache holds for the ledger's active partition,
/// with the visibility the server gave it. The data documents are per class already, so nothing here widens them.
/// <list type="bullet">
/// <item>Only a <see cref="StoreState.Ready"/> store has walls; locked, updating, unavailable, kiosk and empty hold none,
/// and every state change drops the walls read so far.</item>
/// <item>A wall's documents are read on first use (<see cref="GetWallAsync"/>) in one pass under the snapshot's label;
/// a refresh that landed meanwhile discards the read, which is retried against the new snapshot.</item>
/// <item>Refreshes never overlap, and only the newest read lands (<see cref="RefreshAsync"/>); a read that changes
/// nothing changes nothing, not even <see cref="Changed"/>.</item>
/// <item>A wall changes only by swapping its immutable <see cref="CachedWall"/>: a replaced document
/// (<see cref="ReplaceDocument"/>) or a feed event (<see cref="Apply"/>), both for the sync loop (PR 3.12).</item>
/// <item>The viewer's marks exist only on a <c>u-</c> partition. A wall's class is what was cached (the row's
/// <see cref="StoreWallRow.Member"/>), never the page's URL.</item>
/// </list>
/// </summary>
public sealed class ClientStore
{
    // A first read is retried this often while refreshes keep landing during it.
    private const int ReadAttempts = 3;

    private readonly IClientStoreSource source;
    private StoreSnapshot snapshot = StoreSnapshot.Nothing("loading");
    private Dictionary<Guid, CachedWall> walls = [];
    private int generation;
    private Task? started;
    private Task? refreshing;
    private bool refreshAgain;

    /// <summary>Initializes a new instance of the <see cref="ClientStore"/> class.</summary>
    /// <param name="source">The device cache.</param>
    public ClientStore(IClientStoreSource source)
    {
        this.source = source;
    }

    /// <summary>Raised after anything the client shows may have changed.</summary>
    public event Action? Changed;

    /// <summary>Gets what the client may show.</summary>
    public StoreState State { get; private set; } = StoreState.Loading;

    /// <summary>Gets the ledger's active label the store reads, or null.</summary>
    public string? Partition => State == StoreState.Ready ? snapshot.Partition : null;

    /// <summary>Gets the cached walls the page may read; empty unless ready.</summary>
    public IReadOnlyList<StoreWallRow> Walls => State == StoreState.Ready ? snapshot.Walls : [];

    /// <summary>Reads the device once and listens for changes; later calls wait for the same start.</summary>
    /// <returns>A task that completes after the first read.</returns>
    public Task StartAsync() => started ??= StartOnceAsync();

    /// <summary>
    /// Reads what the client may show again; the walls read so far go unless their label and ledger row stand. A refresh
    /// asked for while one runs joins it: that one reads once more, and only its newest read lands, so an older snapshot
    /// (one read just before another tab locked) can never land last.
    /// </summary>
    /// <returns>A task that completes once a read begun after this call has landed.</returns>
    public Task RefreshAsync()
    {
        if (refreshing is { IsCompleted: false })
        {
            refreshAgain = true;
            return refreshing;
        }

        refreshing = RefreshLoopAsync();
        return refreshing;
    }

    /// <summary>The class a wall's cached documents were built for.</summary>
    /// <param name="wallId">The wall.</param>
    /// <returns>Kiosk on a <c>k-</c> label, outsider on <c>anon</c>, else what the row was cached as.</returns>
    public StoreAudience AudienceOf(Guid wallId) => AudienceOf(snapshot, wallId);

    /// <summary>A wall's cached data, read on first use.</summary>
    /// <param name="wallId">The wall.</param>
    /// <returns>The wall, or null when the store is not ready, holds no such wall, or its label changed meanwhile.</returns>
    public async Task<CachedWall?> GetWallAsync(Guid wallId)
    {
        for (var attempt = 0; attempt < ReadAttempts; attempt++)
        {
            if (State != StoreState.Ready || snapshot.Walls.All(w => w.WallId != wallId))
            {
                return null;
            }

            if (walls.TryGetValue(wallId, out var cached))
            {
                return cached;
            }

            var asked = generation;
            var partition = snapshot.Partition!;
            var docs = await source.DocsAsync(partition, wallId);
            if (asked != generation)
            {
                continue;
            }

            if (docs is null || docs.Partition != partition || docs.WallId != wallId)
            {
                await RefreshAsync();
                return null;
            }

            var wall = Read(docs, AudienceOf(snapshot, wallId), MineOf(snapshot, wallId));
            walls = new Dictionary<Guid, CachedWall>(walls) { [wallId] = wall };
            return wall;
        }

        return null;
    }

    /// <summary>"Clear offline data": the device's partition (and a locked one) go; the store then shows nothing.</summary>
    /// <returns>True when the clear ran.</returns>
    public async Task<bool> ClearAsync()
    {
        var cleared = await source.ClearAsync();
        await RefreshAsync();
        return cleared;
    }

    /// <summary>
    /// A new sha of one of a loaded wall's documents (the sync loop's index refetch): it replaces the old one as a
    /// whole. Ignored for a wall that is not loaded, or when the store is not ready for <paramref name="partition"/>.
    /// </summary>
    /// <param name="partition">The label the document was fetched under.</param>
    /// <param name="wallId">The wall.</param>
    /// <param name="kind">The document kind.</param>
    /// <param name="sha">Its sha.</param>
    /// <param name="json">Its JSON.</param>
    /// <returns>True when the wall changed.</returns>
    public bool ReplaceDocument(string partition, Guid wallId, string kind, string sha, string json) =>
        Swap(partition, wallId, wall => wall.WithDocument(kind, sha, json));

    /// <summary>The viewer's marks of a loaded wall, refetched (signed-in only, and only for the store's own label).</summary>
    /// <param name="partition">The label the marks were fetched under.</param>
    /// <param name="document">The marks.</param>
    /// <returns>True when the wall changed.</returns>
    public bool ReplaceMine(string partition, MineDocument document) =>
        IsUser(partition) && document.Partition == partition
        && Swap(partition, document.WallId, wall => wall with { Mine = MineMarks.From(document) });

    /// <summary>A feed event of a loaded wall (the sync loop): applied, and what is left to do.</summary>
    /// <param name="partition">The label the stream was opened under.</param>
    /// <param name="envelope">The event.</param>
    /// <returns>The follow-up; a resync when the store is not ready for that label.</returns>
    public FeedFollowUp Apply(string partition, FeedEnvelope envelope)
    {
        if (Partition != partition || envelope.WallId is not { } wallId || snapshot.Walls.All(w => w.WallId != wallId))
        {
            return FeedFollowUp.Resync;
        }

        if (!walls.TryGetValue(wallId, out var wall))
        {
            // Cached but not opened yet: nothing to change here, only what the event asks of the sync loop.
            var unopened = new CachedWall { WallId = wallId, Audience = AudienceOf(snapshot, wallId) };
            return Masked(partition, StoreFeedApplier.Apply(unopened, envelope).FollowUp);
        }

        var (next, followUp) = StoreFeedApplier.Apply(wall, envelope);
        Swap(partition, wallId, _ => next);
        return Masked(partition, followUp);
    }

    internal static StoreState StateOf(string? state) => state switch
    {
        "ready" => StoreState.Ready,
        "empty" => StoreState.Empty,
        "locked" => StoreState.Locked,
        "updating" => StoreState.Updating,
        "kiosk" => StoreState.Kiosk,
        "loading" => StoreState.Loading,
        _ => StoreState.Unavailable,
    };

    private static bool IsUser(string? partition) => partition is not null && partition.StartsWith("u-", StringComparison.Ordinal);

    // A signed-in device's wall is the members' class only when the row says it was cached as one: a share link opened
    // signed in caches the outsider class, and a later visit without the token must still purge drafts.
    private static StoreAudience AudienceOf(StoreSnapshot snapshot, Guid wallId) => snapshot.Partition switch
    {
        { } p when KioskStoreGate.IsKiosk(p) => StoreAudience.Kiosk,
        "anon" => StoreAudience.Outsider,
        _ when snapshot.Walls.FirstOrDefault(w => w.WallId == wallId) is { Member: true } => StoreAudience.Member,
        _ => StoreAudience.Outsider,
    };

    private static FeedFollowUp Masked(string partition, FeedFollowUp followUp) =>
        IsUser(partition) ? followUp : followUp & ~FeedFollowUp.RefetchMine;

    private static bool SameSnapshot(StoreSnapshot a, StoreSnapshot b) =>
        JsonSerializer.Serialize(a, ClientJsonContext.Default.StoreSnapshot) == JsonSerializer.Serialize(b, ClientJsonContext.Default.StoreSnapshot);

    private static MineMarks MineOf(StoreSnapshot snapshot, Guid wallId) =>
        IsUser(snapshot.Partition) && snapshot.Mine.FirstOrDefault(m => m.WallId == wallId) is { } row ? MineMarks.From(row) : MineMarks.None;

    // The same instance while the marks were read at the same cursor, so an unchanged refresh re-renders nothing.
    private static CachedWall WithMine(CachedWall wall, MineMarks mine) =>
        mine.Cursor == wall.Mine.Cursor && mine.Count == wall.Mine.Count ? wall : wall with { Mine = mine };

    // A loaded wall stands while its ledger row is the one it was read from (the same key: the same document shas, a
    // predecessor standing in or a kind not cached included) and its class is the same.
    private static bool Unchanged(CachedWall wall, StoreSnapshot next) =>
        next.Walls.FirstOrDefault(r => r.WallId == wall.WallId) is { Key: { } key }
        && key == wall.Source
        && AudienceOf(next, wall.WallId) == wall.Audience;

    private static CachedWall Read(StoreDocs docs, StoreAudience audience, MineMarks mine)
    {
        var wall = new CachedWall { WallId = docs.WallId, Audience = audience, Mine = mine, Source = docs.Key };
        foreach (var kind in DataDocumentKinds.All)
        {
            if (docs.Docs.TryGetValue(kind, out var doc))
            {
                wall = wall.WithDocument(kind, doc.Sha, doc.Text);
            }
        }

        return wall;
    }

    private async Task RefreshLoopAsync()
    {
        while (true)
        {
            refreshAgain = false;
            var next = await source.SnapshotAsync();
            if (refreshAgain)
            {
                continue;
            }

            Land(next);
            if (!refreshAgain)
            {
                return;
            }
        }
    }

    private void Land(StoreSnapshot next)
    {
        var state = KioskStoreGate.Check(StateOf(next.State), next);
        if (state == State && SameSnapshot(next, snapshot))
        {
            return;
        }

        var kept = state == StoreState.Ready && next.Partition == snapshot.Partition
            ? walls.Where(w => Unchanged(w.Value, next)).ToDictionary(w => w.Key, w => WithMine(w.Value, MineOf(next, w.Key)))
            : [];
        generation++;
        snapshot = next;
        walls = kept;
        State = state;
        Changed?.Invoke();
    }

    private async Task StartOnceAsync()
    {
        await RefreshAsync();
        await source.ListenAsync(RefreshAsync);
    }

    private bool Swap(string partition, Guid wallId, Func<CachedWall, CachedWall> change)
    {
        if (Partition != partition || !walls.TryGetValue(wallId, out var wall))
        {
            return false;
        }

        var next = change(wall);
        if (ReferenceEquals(next, wall))
        {
            return false;
        }

        walls = new Dictionary<Guid, CachedWall>(walls) { [wallId] = next };
        Changed?.Invoke();
        return true;
    }
}
