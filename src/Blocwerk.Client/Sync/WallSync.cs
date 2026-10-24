// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Client.Store;
using Blocwerk.Contracts.Feed;

namespace Blocwerk.Client.Sync;

/// <summary>
/// One open wall's sync loop while the tab is visible (plan §1, sync loop; PR 3.12):
/// <list type="number">
/// <item>reload (<see cref="WallRefresh"/>), then resume after the stored cursor (<see cref="WallCursorKeeper"/>), or
/// the documents' oldest build cursor when there is none;</item>
/// <item>open the stream and apply each event once, through <see cref="ClientStore.Apply"/>, unless the document it
/// changes was built after it: the comment kinds at once, structural kinds refetch the index 10 s after a burst began,
/// the mine kinds refetch the marks 2 s after, the resync kinds reload everything;</item>
/// <item>a closed stream probes <c>/changes</c> (WallSync.CatchUp.cs); a stream that failed before it delivered anything
/// is opened again only after 30 s or more;</item>
/// <item><see cref="Stop"/> (the tab hid, the wall closed, a refusal) closes the stream and ends every wait.</item>
/// </list>
/// </summary>
internal sealed partial class WallSync : ISyncStreamSink
{
    private readonly ClientStore store;
    private readonly ISyncTransport transport;
    private readonly ISyncClock clock;
    private readonly WallRefresh refresh;
    private readonly WallCursorKeeper keeper;
    private readonly SyncQueue queue = new();
    private readonly SyncWait wait;
    private readonly SyncBackoff backoff;
    private readonly SyncDebounce indexDebounce;
    private readonly SyncDebounce mineDebounce;
    private readonly Action<WallSync>? changed;
    private IDisposable? stream;
    private string? cursor;
    private int run;
    private bool opened;
    private bool heard;
    private int refusedRun;
    private int silentRun;
    private long indexWanted;
    private long indexDone;
    private long mineWanted;
    private long mineDone;

    public WallSync(ClientStore store, ISyncTransport transport, ISyncClock clock, ISyncRefusals refusals, string partition, Guid wallId, string? token, Action<WallSync>? changed = null)
    {
        this.store = store;
        this.transport = transport;
        this.clock = clock;
        Partition = partition;
        WallId = wallId;
        Token = token;
        this.changed = changed;
        refresh = new WallRefresh(store, transport, refusals, partition, wallId, token);
        keeper = new WallCursorKeeper(transport, partition, wallId);
        wait = new SyncWait(clock);
        backoff = new SyncBackoff(clock, transport, wait);
        indexDebounce = new SyncDebounce(clock, SyncDebounce.IndexDelay);
        mineDebounce = new SyncDebounce(clock, SyncDebounce.MineDelay);
    }

    public string Partition { get; }

    public Guid WallId { get; }

    public string? Token { get; }

    /// <summary>Gets the cursor applied so far.</summary>
    public string? Cursor => cursor;

    /// <summary>Gets the newest cursor the device's documents and marks reflect (the one stored for the next start).</summary>
    public string? Persisted => keeper.Persisted;

    /// <summary>Gets a value indicating whether the loop stopped (the tab hid, a refusal, the wall left the device).</summary>
    public bool Stopped { get; private set; }

    /// <summary>Gets a value indicating whether the stream is open now.</summary>
    public bool Live => stream is not null;

    /// <summary>Gets the work queued so far (tests): completes when the loop is idle.</summary>
    public Task Idle => queue.Idle;

    /// <summary>Starts the loop: a reload, then the stream after the resume cursor.</summary>
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

            var saved = await keeper.LoadAsync();
            var (keep, _, marks, replay) = await refresh.RunAsync();
            if (!keep)
            {
                StopIf(mine);
                return;
            }

            if (mine == run)
            {
                cursor = ResumeAt(saved, replay);
                if (marks)
                {
                    keeper.Advance(cursor);
                }
                else
                {
                    // The marks did not arrive: they stay owed, and a retry lets the resume cursor move once they land.
                    mineWanted++;
                    mineDebounce.Schedule(() => queue.Enqueue(() => MineAsync(mine)));
                }

                Open();
            }
        });
    }

    /// <summary>Closes the stream and ends every wait; queued work of this run does nothing.</summary>
    public void Stop()
    {
        if (Stopped)
        {
            return;
        }

        run++;
        Stopped = true;
        CloseStream();
        wait.Cancel();
        indexDebounce.Cancel();
        mineDebounce.Cancel();
        changed?.Invoke(this);
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

    // The stored cursor when it is of the documents' epoch and not older than them (null replay: a document did not
    // arrive, resync). A cursor of an earlier epoch would only cost the server a resync.
    private static string? ResumeAt(string? saved, string? replay) =>
        replay is not null && saved is not null && SyncCursor.SameEpoch(saved, replay) && SyncCursor.IsAfter(saved, replay) ? saved : replay;

    private async Task HandleAsync(int mine, string type, string? data)
    {
        switch (type)
        {
            case "message" when SyncJson.Read(data, SyncJsonContext.Default.FeedEnvelope) is { } envelope:
                Heard();
                await ApplyAsync(mine, envelope);
                break;
            case FeedStreamEvents.Resync when SyncJson.Read(data, SyncJsonContext.Default.FeedChanges) is { } changes:
                Heard();
                await StreamResyncAsync(mine, changes.Next);
                break;
            case FeedStreamEvents.Bye:
                Heard();
                CloseStream();
                await wait.WaitAsync(SyncDelays.Bye(clock.NextJitter()), isNetwork: false);
                await CatchUpAsync(mine);
                break;
            case "open":
                opened = true;
                break;
            case "error":
                Failed();
                CloseStream();
                await CatchUpAsync(mine);
                break;
        }
    }

    private void Heard()
    {
        heard = true;
        refusedRun = 0;
        silentRun = 0;
    }

    // A stream that never opened was refused (a cap's 429, which an EventSource hides); one that opened and closed
    // without a word, twice in a row, is blocked on the way (a proxy that does not pass SSE). A quiet stream that drops
    // once is neither.
    private void Failed()
    {
        if (!opened)
        {
            refusedRun++;
        }
        else if (!heard)
        {
            silentRun++;
        }
    }

    /// <summary>Gets the stream failures in a row that make the next open wait (<see cref="SyncDelays.StreamRefused"/>).</summary>
    private int QuickFailures => refusedRun + Math.Max(0, silentRun - 1);

    private void StopIf(int mine)
    {
        if (mine == run)
        {
            Stop();
        }
    }

    private void Open()
    {
        stream?.Dispose();
        opened = false;
        heard = false;
        stream = transport.OpenStream(WallId, cursor, Token, this);
        changed?.Invoke(this);
    }

    private void CloseStream()
    {
        if (stream is null)
        {
            return;
        }

        stream.Dispose();
        stream = null;
        changed?.Invoke(this);
    }
}
