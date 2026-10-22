// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Collections.Concurrent;
using System.Threading.Channels;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Feed;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Blocwerk.Core.Pulse;

/// <summary>
/// The in-process <see cref="IWallPulse"/>. Publishing only updates a throttle slot and queues the pulse; one loop hands
/// queued pulses to the wall's subscribers, so a job never waits for a circuit and a slow handler never reorders a job's
/// pulses. A slot is dropped when its job ends; slots of jobs that never said so are swept once they are idle.
/// </summary>
public sealed class WallPulseHub : IWallPulse, IDisposable
{
    /// <summary>The default throttle: at most four pulses per second per job.</summary>
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromMilliseconds(250);

    // A slot is a few dozen bytes and lives from a job's first pulse to its ended one. Jobs that never say they ended
    // (tracked rows without an end state, a process step that died) leave theirs: the sweep runs only once more than
    // SweepAbove slots exist, which a single instance's handful of concurrent jobs never reaches in normal use, and then
    // drops every slot idle for IdleSlot. The bound keeps the common publish free of the scan; IdleSlot is far above any
    // throttle interval, so a live job's slot is never swept.
    private const int SweepAbove = 512;
    private static readonly TimeSpan IdleSlot = TimeSpan.FromMinutes(10);

    private readonly Lock gate = new();
    private readonly Dictionary<(Guid Wall, string Topic, Guid Job), WallPulseSlot> slots = [];
    private readonly ConcurrentDictionary<Guid, WallPulseSubscriber[]> subscribers = new();
    private readonly Channel<WallPulse> queue = Channel.CreateUnbounded<WallPulse>(new UnboundedChannelOptions { SingleReader = true });
    private readonly TimeProvider time;
    private readonly ILogger logger;
    private int disposed;

    public WallPulseHub(ILogger<WallPulseHub>? logger = null, TimeProvider? clock = null, TimeSpan? interval = null)
    {
        this.logger = (ILogger?)logger ?? NullLogger.Instance;
        time = clock ?? TimeProvider.System;
        Interval = interval ?? DefaultInterval;
        _ = Task.Run(DeliverLoopAsync);
    }

    /// <summary>The shortest gap between two pulses of one job.</summary>
    public TimeSpan Interval { get; }

    /// <summary>The throttle slots held now (tests).</summary>
    internal int SlotCount
    {
        get
        {
            lock (gate)
            {
                return slots.Count;
            }
        }
    }

    public void Publish(WallPulse pulse)
    {
        try
        {
            if (pulse.Audience != FeedAudience.Setters)
            {
                // Every job pulse is wall-admin workflow (design §5.6, PR 1.7 review N1): a wider audience is a bug.
                logger.LogWarning("A {Topic} pulse of wall {WallId} for {Audience} was dropped: job pulses go to Setters only", pulse.Topic, pulse.WallId, pulse.Audience);
                return;
            }

            if (Volatile.Read(ref disposed) == 0 && pulse.WallId != Guid.Empty)
            {
                Throttle(pulse);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "A {Topic} pulse of wall {WallId} was dropped", pulse.Topic, pulse.WallId);
        }
    }

    public IDisposable Subscribe(Guid wallId, Func<FeedViewer?> viewer, Action<WallPulse> onPulse)
    {
        var subscriber = new WallPulseSubscriber(wallId, viewer, onPulse, Remove);
        subscribers.AddOrUpdate(wallId, _ => [subscriber], (_, list) => [.. list, subscriber]);
        return subscriber;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 1)
        {
            return;
        }

        queue.Writer.TryComplete();
        lock (gate)
        {
            foreach (var slot in slots.Values)
            {
                slot.Timer?.Dispose();
            }

            slots.Clear();
        }
    }

    // Under the gate: send now when the interval since the last one has passed, else hold the latest for the timer.
    private void Throttle(WallPulse pulse)
    {
        var key = (pulse.WallId, pulse.Topic, pulse.JobId);
        lock (gate)
        {
            var now = time.GetUtcNow();
            if (pulse.Ended)
            {
                if (slots.Remove(key, out var ended))
                {
                    ended.Timer?.Dispose();
                }

                queue.Writer.TryWrite(pulse);
                return;
            }

            if (!slots.TryGetValue(key, out var slot))
            {
                Sweep(now);
                slots[key] = slot = new WallPulseSlot();
            }

            if (slot.Timer is null && now - slot.LastSent >= Interval)
            {
                slot.LastSent = now;
                queue.Writer.TryWrite(pulse);
                return;
            }

            slot.Held = pulse;
            slot.Timer ??= time.CreateTimer(_ => SendHeld(key, slot), null, Interval - (now - slot.LastSent), Timeout.InfiniteTimeSpan);
        }
    }

    private void SendHeld((Guid Wall, string Topic, Guid Job) key, WallPulseSlot slot)
    {
        lock (gate)
        {
            slot.Timer?.Dispose();
            slot.Timer = null;
            if (!slots.TryGetValue(key, out var current) || current != slot || slot.Held is not { } held)
            {
                return; // the job ended (or the hub went) in the meantime
            }

            slot.Held = null;
            slot.LastSent = time.GetUtcNow();
            queue.Writer.TryWrite(held);
        }
    }

    // Under the gate: a job that never said it ended (a crashed process step) leaves its slot; idle ones go.
    private void Sweep(DateTimeOffset now)
    {
        if (slots.Count < SweepAbove)
        {
            return;
        }

        foreach (var (key, slot) in slots.Where(s => s.Value.Timer is null && now - s.Value.LastSent > IdleSlot).ToList())
        {
            slots.Remove(key);
        }
    }

    private void Remove(WallPulseSubscriber subscriber)
    {
        while (subscribers.TryGetValue(subscriber.WallId, out var list))
        {
            var rest = list.Where(s => s != subscriber).ToArray();
            var done = rest.Length == 0
                ? subscribers.TryRemove(new KeyValuePair<Guid, WallPulseSubscriber[]>(subscriber.WallId, list))
                : subscribers.TryUpdate(subscriber.WallId, rest, list);
            if (done)
            {
                return;
            }
        }
    }

    private async Task DeliverLoopAsync()
    {
        await foreach (var pulse in queue.Reader.ReadAllAsync())
        {
            if (!subscribers.TryGetValue(pulse.WallId, out var list))
            {
                continue;
            }

            foreach (var subscriber in list)
            {
                try
                {
                    subscriber.Deliver(pulse);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "A pulse subscriber of wall {WallId} threw", pulse.WallId);
                }
            }
        }
    }
}
