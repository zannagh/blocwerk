// <copyright file="UnreadableStream.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.Tests.Refresh;

/// <summary>A request body that must not be read.</summary>
internal sealed class UnreadableStream : MemoryStream
{
    public override int Read(byte[] buffer, int offset, int count) => throw new InvalidOperationException("The body was read.");

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("The body was read.");

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("The body was read.");
}
