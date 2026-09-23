// <copyright file="ObservingReadStream.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.Tests;

/// <summary>
/// A read-only stream over <paramref name="inner"/> that calls <paramref name="onRead"/> before every
/// read, so a test can look at server state while an upload is still streaming (or throw from it to
/// simulate an aborted request).
/// </summary>
internal sealed class ObservingReadStream(Stream inner, Action onRead) : Stream
{
    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        onRead();
        return inner.Read(buffer, offset, count);
    }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        onRead();
        return inner.ReadAsync(buffer, cancellationToken);
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        onRead();
        return inner.ReadAsync(buffer, offset, count, cancellationToken);
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            inner.Dispose();
        }

        base.Dispose(disposing);
    }
}
