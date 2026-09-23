// <copyright file="CaptureVideoUploadSlotsTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Capture;

namespace Blocwerk.Core.Tests;

/// <summary>
/// Walk-along video uploads are bounded: one streaming upload per user and a few server-wide, so a
/// wall admin cannot open dozens of parallel 2 GB uploads to fill the disk or pin the deploy gate.
/// </summary>
public class CaptureVideoUploadSlotsTests
{
    [Fact]
    public void OneSlotPerUser_ReleasedOnDispose()
    {
        var slots = new CaptureVideoUploadSlots();
        var user = Guid.NewGuid();

        var first = slots.TryAcquire(user);
        Assert.NotNull(first);
        Assert.Null(slots.TryAcquire(user));
        Assert.NotNull(slots.TryAcquire(Guid.NewGuid()));

        first.Dispose();
        first.Dispose(); // idempotent: must not free a slot twice
        using var again = slots.TryAcquire(user);
        Assert.NotNull(again);
        Assert.Null(slots.TryAcquire(user));
    }

    [Fact]
    public void ServerWideCap_RefusesFurtherUsers()
    {
        var slots = new CaptureVideoUploadSlots(global: 2);
        using var a = slots.TryAcquire(Guid.NewGuid());
        var b = slots.TryAcquire(Guid.NewGuid());

        Assert.NotNull(a);
        Assert.NotNull(b);
        Assert.Null(slots.TryAcquire(Guid.NewGuid()));

        b.Dispose();
        using var c = slots.TryAcquire(Guid.NewGuid());
        Assert.NotNull(c);
    }

    [Fact]
    public async Task SecondUploadBySameUser_IsRefusedWhileTheFirstStreams()
    {
        using var h = new WallTestHarness();
        using var s = new CaptureScenario(h);
        s.SplatClient.IsConfigured = true;
        await h.SeedWallAsync(holdCount: 0);
        await WallGlyphSettingsTests.Service(h).SetGlyphSettingsAsync(h.WallId, true, 125);
        var draft = (await s.Service.CreateDraftAsync(h.WallId)).CaptureId;

        var reading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var slow = new GatedReadStream(FakeVideoFrameExtractor.Mp4Stream(), reading, release.Task);
        var first = s.Service.AddVideoAsync(draft, "walk.mp4", slow, CancellationToken.None);
        await reading.Task.WaitAsync(TimeSpan.FromSeconds(30));

        await using var second = FakeVideoFrameExtractor.Mp4Stream();
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(
            () => s.Service.AddVideoAsync(draft, "again.mp4", second, CancellationToken.None));
        Assert.Contains("Another video upload", refused.Message, StringComparison.Ordinal);

        release.SetResult();
        var info = await first.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal("walk.mp4", info.FileName);

        // The slot is free again once the first upload is done.
        await using var third = FakeVideoFrameExtractor.Mp4Stream();
        await s.Service.AddVideoAsync(draft, "third.mp4", third, CancellationToken.None);
    }

    /// <summary>Signals its first read, then holds it until <c>release</c> completes.</summary>
    private sealed class GatedReadStream(Stream inner, TaskCompletionSource reading, Task release) : Stream
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

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            reading.TrySetResult();
            await release.WaitAsync(cancellationToken);
            return await inner.ReadAsync(buffer, cancellationToken);
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException("async only");

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
}
