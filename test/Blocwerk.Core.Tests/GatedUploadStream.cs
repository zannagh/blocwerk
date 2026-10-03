// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.Tests;

/// <summary>
/// A request body that hands out <c>head</c>, then blocks until <see cref="Release"/> (a stalled or trickling runner),
/// then hands out <c>tail</c>. <see cref="Started"/> completes once the server read the first bytes.
/// </summary>
internal sealed class GatedUploadStream(byte[] head, byte[] tail) : Stream
{
    private readonly TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int headOffset;
    private int tailOffset;

    public Task Started => started.Task;

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => head.Length + tail.Length;

    public override long Position
    {
        get => headOffset + tailOffset;
        set => throw new NotSupportedException();
    }

    public void Release() => gate.TrySetResult();

    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (headOffset < head.Length)
        {
            var n = Math.Min(buffer.Length, head.Length - headOffset);
            head.AsMemory(headOffset, n).CopyTo(buffer);
            headOffset += n;
            started.TrySetResult();
            return n;
        }

        await gate.Task.WaitAsync(cancellationToken);
        var m = Math.Min(buffer.Length, tail.Length - tailOffset);
        tail.AsMemory(tailOffset, m).CopyTo(buffer);
        tailOffset += m;
        return m;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        Release();
        base.Dispose(disposing);
    }
}
