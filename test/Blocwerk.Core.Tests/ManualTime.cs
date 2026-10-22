// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.Tests;

/// <summary>A clock that moves only when the test says so.</summary>
internal sealed class ManualTime : TimeProvider
{
    private DateTimeOffset now = new(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => now;

    public void Advance(TimeSpan by) => now += by;
}
