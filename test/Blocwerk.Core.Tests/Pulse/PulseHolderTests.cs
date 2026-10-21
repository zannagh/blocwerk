// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Enums;
using Blocwerk.Core.Pulse;
using Blocwerk.Web.State.Live;

namespace Blocwerk.Core.Tests.Pulse;

/// <summary>
/// A component's pulse subscription never outlives it (PR 1.7 review W1): nothing opens after dispose, one that lands
/// after the dispose or after a newer open is disposed, and a replaced one goes.
/// </summary>
public sealed class PulseHolderTests
{
    [Fact]
    public void AfterDispose_NothingIsOpened()
    {
        var holder = new PulseHolder<Counted>();
        holder.Dispose();
        var opened = 0;

        Assert.False(holder.Open(Guid.NewGuid(), () => new Counted(() => opened++)));
        Assert.Equal(0, opened); // the factory never ran, so no loop was started
        Assert.Null(holder.Current);
    }

    [Fact]
    public async Task OneThatLandsAfterTheDispose_IsDisposedAtOnce()
    {
        var holder = new PulseHolder<Counted>();
        var landing = new TaskCompletionSource<Counted>();
        var opening = holder.OpenAsync(Guid.NewGuid(), () => landing.Task);
        holder.Dispose();
        var late = new Counted();
        landing.SetResult(late);

        Assert.Null(await opening);
        Assert.Equal(1, late.Disposed);
        Assert.Null(holder.Current);
    }

    [Fact]
    public async Task OnlyTheLatestOfOverlappingOpens_IsKept()
    {
        var holder = new PulseHolder<Counted>();
        var first = new TaskCompletionSource<Counted>();
        var (a, b) = (new Counted(), new Counted());
        var older = holder.OpenAsync(Guid.NewGuid(), () => first.Task);
        var wallB = Guid.NewGuid();
        Assert.Same(b, await holder.OpenAsync(wallB, () => Task.FromResult(b)));
        first.SetResult(a);

        Assert.Null(await older);
        Assert.Equal((1, 0), (a.Disposed, b.Disposed));
        Assert.True(holder.IsFor(wallB));
    }

    [Fact]
    public void Opening_ReplacesAndDisposesThePrevious_AndCloseKeepsTheHolderUsable()
    {
        var holder = new PulseHolder<Counted>();
        var (a, b, c) = (new Counted(), new Counted(), new Counted());
        holder.Open(Guid.NewGuid(), () => a);
        holder.Open(Guid.NewGuid(), () => b);
        Assert.Equal(1, a.Disposed);

        holder.Close();
        Assert.Equal(1, b.Disposed);
        Assert.True(holder.Open(Guid.NewGuid(), () => c));
        holder.Dispose();
        Assert.Equal(1, c.Disposed);
    }

    [Fact]
    public async Task ARealWake_OpenedAfterThePageWent_LeavesNoSubscription()
    {
        await using var f = new CircuitCacheFixture();
        await f.StartAsync();
        using var hub = new WallPulseHub(interval: TimeSpan.Zero);
        var holder = new PulseHolder<PulseWake>();
        PulseWake? wake = null;
        var landing = new TaskCompletionSource();
        var opening = holder.OpenAsync(f.WallId, async () =>
        {
            await landing.Task;
            return wake = await PulseWake.OpenAsync(f.Cache, hub, f.WallId, [PulseTopics.Capture]);
        });
        holder.Dispose();
        landing.SetResult();
        await opening;

        hub.Publish(new WallPulse(f.WallId, PulseTopics.Capture, Guid.NewGuid(), FeedAudience.Setters));
        await Task.Delay(300);
        Assert.Equal(0, wake!.Received);
    }
}
