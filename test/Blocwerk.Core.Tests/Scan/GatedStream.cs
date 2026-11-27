// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.Tests.Scan;

/// <summary>A request body that hands out nothing until <c>gate</c> completes, like a slow upload.</summary>
internal sealed class GatedStream(byte[] bytes, Task gate) : MemoryStream(bytes)
{
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        return await base.ReadAsync(buffer, cancellationToken);
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        return await base.ReadAsync(buffer, offset, count, cancellationToken);
    }
}
