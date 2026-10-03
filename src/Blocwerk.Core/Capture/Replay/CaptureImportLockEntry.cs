// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.Capture.Replay;

/// <summary>One import's lock in <see cref="CaptureImportLocks"/> and how many requests hold or wait for it.</summary>
internal sealed class CaptureImportLockEntry
{
    public SemaphoreSlim Gate { get; } = new(1, 1);

    public int Users { get; set; }
}
