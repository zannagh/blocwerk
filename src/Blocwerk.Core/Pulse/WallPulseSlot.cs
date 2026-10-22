// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.Pulse;

/// <summary>The throttle state of one (wall, topic, job): when it last went out, and the latest pulse held back since.</summary>
internal sealed class WallPulseSlot
{
    public DateTimeOffset LastSent { get; set; } = DateTimeOffset.MinValue;

    /// <summary>The latest pulse that arrived inside the interval, to go out when it ends.</summary>
    public WallPulse? Held { get; set; }

    /// <summary>The timer that sends <see cref="Held"/>; null when none is due.</summary>
    public ITimer? Timer { get; set; }
}
