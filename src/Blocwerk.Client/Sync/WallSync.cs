// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Client.Store;
using Blocwerk.Contracts.Feed;

namespace Blocwerk.Client.Sync;

/// <summary>
/// One open wall's sync loop while the tab is visible (plan §1, sync loop; PR 3.12):
/// <list type="number">
/// <item>reload (<see cref="WallRefresh"/>) and hold the documents' oldest cursor;</item>
/// <item>open the stream after it and apply each event once, through <see cref="ClientStore.Apply"/>: the comment
/// kinds at once, structural kinds refetch the index 10 s after a burst began, the mine kinds refetch the marks, the
/// resync kinds reload everything;</item>
/// <item>a closed stream (a bye, an error, a refused reconnect) probes <c>/changes</c> from the cursor: pages while
/// <c>more</c>, then reopens; a 429 waits <c>Retry-After</c> plus 0–20 %; a refusal goes to the identity matrix; no
/// network backs off 5/30/120 s ±20 %, cut short by <c>online</c> to a random 0–10 s;</item>
/// <item><see cref="Stop"/> (the tab hid, the wall closed) closes the stream and ends every wait.</item>
/// </list>
/// </summary>
internal sealed class WallSync : ISyncStreamSink
{
    private readonly ClientStore store;
    private readonly ISyncTransport transport;
    private readonly ISyncClock clock;
    private readonly WallRefresh refresh;
    private readonly SyncQueue queue = new();
    private readonly SyncWait wait;
    private readonly SyncBackoff backoff;
    private readonly SyncIndexDebounce debounce;
    private readonly Action? liveChanged;
    private IDisposable? stream;
    private string? cursor;
    private int run;

    public WallSync(ClientStore store, ISyncTransport transport, ISyncClock clock, ISyncRefusals refusals, string partition, Guid wallId, string? token, Action? liveChanged = null)
    {
        this.store = store;
        this.transport = transport;
        this.clock = clock;
        Partition = partition;
        WallId = wallId;
        Token = token;
        this.liveChanged = liveChanged;
        refresh = new WallRefresh(store, transport, refusals, partition, wallId, token);
        wait = new SyncWait(clock);
        backoff = new SyncBackoff(clock, transport, wait);
        debounce = new SyncIndexDebounce(clock, () => queue.Enqueue(() => IndexAsync(run)));
    }

    public string Partition { get; }

    public Guid WallId { get; }

    public string? Token { get; }

    /// <summary>Gets the cursor applied so far.</summary>
    public string? Cursor => cursor;

    /// <summary>Gets a value indicating whether the loop stopped (the tab hid, a refusal, the wall left the device).</summary>
    public bool Stopped { get; private set; }

    /// <summary>Gets a value indicating whether the stream is open now.</summary>
    public bool Live => stream is not null;

    /// <summary>Gets the work queued so far (tests): completes when the loop is idle.</summary>
    public Task Idle => queue.Idle;

    /// <summary>Starts (or, after <see cref="Stop"/>, restarts) the loop: a reload, then the stream.</summary>
    /// <returns>A task that completes once the stream was opened or the wall stopped.</returns>
    public Task StartAsync()
    {
        var mine = ++run;
        Stopped = false;
        return queue.Enqueue(async () =>
        {
            backoff.Reset();
            if (mine != run || await store.GetWallAsync(WallId) is null)
            {
                return;
            }

            var (keep, replay) = await refresh.RunAsync();
            if (keep && mine == run)
            {
                cursor = replay;
                Open();
            }
        });
    }

    /// <summary>Closes the stream and ends every wait; queued work of this run does nothing.</summary>
    public void Stop()
    {
        run++;
        Stopped = true;
        CloseStream();
        wait.Cancel();
        debounce.Cancel();
    }

    /// <summary>The browser came back online: a network backoff ends, for a retry after a random 0–10 s.</summary>
    public void Online() => wait.Online();

    /// <summary>
    /// Queues a stream message and returns at once: the stream never waits on the loop (a catch-up may back off for
    /// minutes), and the queue keeps the messages in order.
    /// </summary>
    /// <param name="type">The message type.</param>
    /// <param name="data">Its JSON, or null.</param>
    /// <returns>A completed task.</returns>
    public Task OnMessageAsync(string type, string? data)
    {
        if (Stopped)
        {
            return Task.CompletedTask;
        }

        var mine = run;
        _ = queue.Enqueue(() => mine == run ? HandleAsync(mine, type, data) : Task.CompletedTask);
        return Task.CompletedTask;
    }

    private async Task HandleAsync(int mine, string type, string? data)
    {
        switch (type)
        {
            case "message" when SyncJson.Read(data, SyncJsonContext.Default.FeedEnvelope) is { } envelope:
                await ApplyAsync(mine, envelope);
                break;
            case FeedStreamEvents.Resync when SyncJson.Read(data, SyncJsonContext.Default.FeedChanges) is { } changes:
                await ResyncAsync(mine, changes.Next);
                break;
            case FeedStreamEvents.Bye:
            case "error":
                CloseStream();
                await CatchUpAsync(mine);
                break;
        }
    }

    // Each event once: the replay after a reconnect and a hub that already tracked the wall can both deliver a row.
    private async Task ApplyAsync(int mine, FeedEnvelope envelope)
    {
        if (cursor is not null && !SyncCursor.IsAfter(envelope.Cursor, cursor))
        {
            return;
        }

        var followUp = SyncPayload.IsUnknown(envelope) ? FeedFollowUp.Resync : store.Apply(Partition, envelope);
        cursor = envelope.Cursor;
        if (followUp.HasFlag(FeedFollowUp.Resync))
        {
            await ReloadAsync(mine);
            return;
        }

        if (followUp.HasFlag(FeedFollowUp.RefetchIndex))
        {
            debounce.Schedule();
        }

        if (followUp.HasFlag(FeedFollowUp.RefetchMine))
        {
            await refresh.MineAsync();
        }
    }

    // A resync (the stream's or a catch-up page's): reload, then continue after the server's mark.
    private async Task ResyncAsync(int mine, string next)
    {
        await ReloadAsync(mine);
        if (mine == run)
        {
            cursor = next;
        }
    }

    private async Task ReloadAsync(int mine)
    {
        var (keep, _) = await refresh.RunAsync();
        if (!keep && mine == run)
        {
            Stop();
        }
    }

    private async Task IndexAsync(int mine)
    {
        if (mine == run && !await refresh.IndexAsync() && mine == run)
        {
            Stop();
        }
    }

    // The stream closed: page /changes from the cursor until the gap is closed, then reopen.
    private async Task CatchUpAsync(int mine)
    {
        while (mine == run)
        {
            var page = await transport.ChangesAsync(WallId, cursor, Token);
            if (mine != run)
            {
                return;
            }

            if (page.Status == 429)
            {
                await wait.WaitAsync(SyncDelays.RetryAfter(page.RetryAfter, clock.NextJitter()), isNetwork: false);
                continue;
            }

            if (SyncLabels.IsRefusal(page.Status))
            {
                Stop();
                await refresh.RefusedAsync(page.Status, page.Reason);
                return;
            }

            var changes = page.Status == 200 ? SyncJson.Read(page.Body, SyncJsonContext.Default.FeedChanges) : null;
            if (changes is null)
            {
                await backoff.FailedAsync();
                continue;
            }

            backoff.Reached();
            if (await PageAsync(mine, changes) && mine == run)
            {
                Open();
                return;
            }
        }
    }

    // One catch-up page; true when it was the last one.
    private async Task<bool> PageAsync(int mine, FeedChanges changes)
    {
        if (changes.Resync)
        {
            await ResyncAsync(mine, changes.Next);
            return true;
        }

        foreach (var envelope in changes.Events)
        {
            if (mine != run)
            {
                return true;
            }

            await ApplyAsync(mine, envelope);
        }

        if (mine == run && SyncCursor.IsAfter(changes.Next, cursor))
        {
            cursor = changes.Next;
        }

        return !changes.More;
    }

    private void Open()
    {
        stream?.Dispose();
        stream = transport.OpenStream(WallId, cursor, Token, this);
        liveChanged?.Invoke();
    }

    private void CloseStream()
    {
        if (stream is null)
        {
            return;
        }

        stream.Dispose();
        stream = null;
        liveChanged?.Invoke();
    }
}
