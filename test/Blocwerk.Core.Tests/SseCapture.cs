// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Text;
using System.Text.Json;
using Blocwerk.Contracts.Feed;

namespace Blocwerk.Core.Tests;

/// <summary>
/// A response body a running event stream writes into while the test reads it: thread-safe, append-only, and parsed into
/// its <see cref="SseFrame"/>s on demand.
/// </summary>
internal sealed class SseCapture : Stream
{
    private readonly MemoryStream buffer = new();

    public override bool CanRead => false;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => throw new NotSupportedException();

    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public string Text
    {
        get
        {
            lock (buffer)
            {
                return Encoding.UTF8.GetString(buffer.ToArray());
            }
        }
    }

    /// <summary>Gets the complete frames written so far (comments and the <c>retry:</c> preamble included).</summary>
    public IReadOnlyList<SseFrame> Frames => SseFrame.Parse(Text);

    /// <summary>Gets the envelopes of the default events.</summary>
    public IReadOnlyList<FeedEnvelope> Events => Frames
        .Where(f => f.Event is null && f.Data is not null)
        .Select(f => JsonSerializer.Deserialize<FeedEnvelope>(f.Data!, FeedJson.Options)!)
        .ToList();

    public async Task WaitUntilAsync(Func<SseCapture, bool> condition, double seconds = 10)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition(this))
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("The stream did not get there in time. It wrote:\n" + Text);
            }

            await Task.Delay(20);
        }
    }

    public override void Write(byte[] data, int offset, int count)
    {
        lock (buffer)
        {
            buffer.Write(data, offset, count);
        }
    }

    public override Task WriteAsync(byte[] data, int offset, int count, CancellationToken cancellationToken)
    {
        Write(data, offset, count);
        return Task.CompletedTask;
    }

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        lock (buffer)
        {
            buffer.Write(data.Span);
        }

        return ValueTask.CompletedTask;
    }

    public override void Flush()
    {
    }

    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public override int Read(byte[] data, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();
}
