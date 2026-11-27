// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Capture.Scan;

namespace Blocwerk.Web.Endpoints;

/// <summary>
/// Reads at most <paramref name="limit"/> bytes of <paramref name="inner"/>, then refuses with 413: a request body read by a
/// deserializer stops at the limit even when no Content-Length was sent and the server's own body limit cannot be set.
/// </summary>
public sealed class BoundedReadStream(Stream inner, long limit) : Stream
{
    private long read;

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => read;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => Count(inner.Read(buffer, offset, Window(count)));

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        Count(await inner.ReadAsync(buffer[..Window(buffer.Length)], cancellationToken));

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    /// <summary>One byte beyond the limit is asked for, so a body of exactly the limit ends cleanly and a longer one is seen.</summary>
    private int Window(int count) => (int)Math.Min(count, limit + 1 - read);

    private int Count(int n)
    {
        read += n;
        if (read > limit)
        {
            throw new ScanUploadException(ScanUploadException.TooLarge, $"The manifest is larger than {limit / (1024 * 1024)} MB.");
        }

        return n;
    }
}
