// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Client.Store;

namespace Blocwerk.Client.Sync;

/// <summary>
/// The offline client's sync (Phase 3 plan §1, PR 3.12): the wall the page shows is kept live while the tab is
/// visible (<see cref="WallSync"/>), and a signed-in device polls its own rows (<see cref="MeChangesPoll"/>) when the
/// tab becomes visible or the browser comes back online. Only a ready store under <c>u-</c> or a share link's
/// <c>anon</c> syncs; a kiosk polls nothing, and a lock, a wipe or another identity stops everything.
/// Refusals follow the identity matrix (§3): 403/404 drop the wall, a 401 <c>expired</c> locks a <c>u-</c> label,
/// any other 401 and any 410 wipe; the store then re-reads what is left.
/// </summary>
public sealed class ClientSync : ISyncSignals, ISyncRefusals
{
    private readonly ClientStore store;
    private readonly ISyncTransport transport;
    private readonly ISyncClock clock;
    private readonly MeChangesPoll me;
    private SyncTarget target = SyncTarget.None;
    private WallSync? wall;
    private bool visible = true;
    private bool started;
    private string? partitionSeen;

    /// <summary>Initializes a new instance of the <see cref="ClientSync"/> class.</summary>
    /// <param name="store">The client's store.</param>
    /// <param name="transport">The server and the device cache.</param>
    /// <param name="clock">Time and jitter.</param>
    public ClientSync(ClientStore store, ISyncTransport transport, ISyncClock clock)
    {
        this.store = store;
        this.transport = transport;
        this.clock = clock;
        me = new MeChangesPoll(store, transport, clock, this);
        store.Changed += OnStoreChanged;
    }

    /// <summary>Raised when the open wall's sync started, stopped, or its stream opened or closed.</summary>
    public event Action? Changed;

    /// <summary>Gets the wall being synced, or null.</summary>
    public Guid? WallId => wall?.WallId;

    /// <summary>Gets a value indicating whether the open wall's stream is open now.</summary>
    public bool Live => wall?.Live == true;

    /// <summary>Gets the open wall's feed cursor applied so far (tests).</summary>
    internal string? Cursor => wall?.Cursor;

    /// <summary>Gets the work queued for the open wall (tests): completes when its loop is idle.</summary>
    internal Task Idle => wall?.Idle ?? Task.CompletedTask;

    /// <summary>Starts listening (the store must have started): the page's signals, the URL's wall, the own-rows poll.</summary>
    /// <param name="uri">The page's URL.</param>
    /// <returns>A task that completes once the first sync began.</returns>
    public async Task StartAsync(string uri)
    {
        started = true;
        partitionSeen = store.Partition;
        target = SyncTarget.Of(uri);
        visible = await transport.WatchAsync(this);
        await ResumeAsync();
    }

    /// <summary>The page moved (the router): sync the wall the new URL shows, if any.</summary>
    /// <param name="uri">The new URL.</param>
    /// <returns>A task that completes once the wall's sync began or stopped.</returns>
    public Task NavigateAsync(string uri)
    {
        target = SyncTarget.Of(uri);
        return started ? SyncWallAsync() : Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task OnVisibilityAsync(bool shown)
    {
        visible = shown;
        if (!shown)
        {
            StopWall();
            me.Cancel();
            return;
        }

        await ResumeAsync();
    }

    /// <inheritdoc />
    public async Task OnOnlineAsync()
    {
        wall?.Online();
        if (visible)
        {
            await me.PollAsync();
        }
    }

    /// <inheritdoc />
    public async Task RefusedAsync(string partition, Guid wallId, int status, string? reason, bool mine)
    {
        if (SyncLabels.EndsSession(status))
        {
            StopWall();
        }
        else if (!mine && wall?.WallId == wallId)
        {
            StopWall();
        }

        await transport.RefusedAsync(partition, wallId, status, reason, mine);
        await store.RefreshAsync();
    }

    private async Task ResumeAsync()
    {
        if (!visible)
        {
            return;
        }

        await SyncWallAsync();
        await me.PollAsync();
    }

    // Starts the URL's wall when it differs from the running one (or nothing runs), under the store's label now; a wall
    // the device holds nothing of has nothing to keep current.
    private Task SyncWallAsync()
    {
        var partition = store.Partition;
        if (!visible || !SyncLabels.Syncs(partition) || target.WallId is not { } wallId || store.Walls.All(w => w.WallId != wallId))
        {
            StopWall();
            return Task.CompletedTask;
        }

        if (wall is { } running && running.WallId == wallId && running.Partition == partition && running.Token == target.Token && !running.Stopped)
        {
            return Task.CompletedTask;
        }

        StopWall();
        wall = new WallSync(store, transport, clock, this, partition!, wallId, target.Token, OnWallChanged);
        var start = wall.StartAsync();
        Changed?.Invoke();
        return start;
    }

    private void StopWall()
    {
        if (wall is not { } running)
        {
            return;
        }

        wall = null;
        running.Stop();
        Changed?.Invoke();
    }

    // The stream opened or closed, or the loop stopped itself (a refusal, the row gone): a stopped loop is no wall.
    private void OnWallChanged(WallSync changed)
    {
        if (ReferenceEquals(wall, changed) && changed.Stopped)
        {
            wall = null;
        }

        Changed?.Invoke();
    }

    // A lock, a wipe, a Clear or another identity (this tab or another): nothing keeps running under the old label,
    // and a label the store reads now (ready after a start, a sign-in in another tab) syncs the URL's wall and polls.
    private void OnStoreChanged()
    {
        if (!started)
        {
            return;
        }

        if (store.Partition == partitionSeen)
        {
            // Another tab dropped the wall (a 403, an eviction): its stream would hold a server slot for nothing.
            if (wall is { } running && store.Walls.All(w => w.WallId != running.WallId))
            {
                StopWall();
            }

            return;
        }

        partitionSeen = store.Partition;
        StopWall();
        me.Cancel();
        _ = ResumeAsync();
    }
}
