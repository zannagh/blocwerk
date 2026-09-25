// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Runners;

namespace Blocwerk.Core.Tests;

/// <summary>A <see cref="DiskSpaceProbe"/> whose free space the test sets (null: unknown, nothing refused).</summary>
internal sealed class FakeDiskSpace : DiskSpaceProbe
{
    public long? Free { get; set; }

    /// <summary>How often the queue asked.</summary>
    public int Reads { get; private set; }

    /// <summary>Called on every read, before <see cref="Free"/> is returned (e.g. to shrink it while an upload streams).</summary>
    public Action<FakeDiskSpace>? OnRead { get; set; }

    public override long? FreeBytes(string directory)
    {
        Reads++;
        OnRead?.Invoke(this);
        return Free;
    }
}
