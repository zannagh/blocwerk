// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Pulse;

namespace Blocwerk.Core.Tests.Pulse;

/// <summary>What one test subscription received, the drain sentinels left out.</summary>
internal sealed class PulseInbox
{
    /// <summary>The topic of the tests' drain pulses.</summary>
    public const string Sentinel = "sentinel";

    private readonly List<WallPulse> received = [];

    public IDisposable Subscription { get; set; } = default!;

    public IReadOnlyList<WallPulse> Received
    {
        get
        {
            lock (received)
            {
                return [.. received];
            }
        }
    }

    public void Add(WallPulse pulse)
    {
        if (pulse.Topic == Sentinel)
        {
            return;
        }

        lock (received)
        {
            received.Add(pulse);
        }
    }

    public List<double> Progress() => Received.Select(p => p.Progress ?? double.NaN).ToList();
}
