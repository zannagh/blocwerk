// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.Tests.Pulse;

/// <summary>A disposable that counts its disposals (and may run something when created).</summary>
internal sealed class Counted : IDisposable
{
    public Counted(Action? created = null) => created?.Invoke();

    public int Disposed { get; private set; }

    public void Dispose() => Disposed++;
}
