// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Web.Components.Shared;

namespace Blocwerk.Core.Tests;

/// <summary>The mobile-data notice asks once per stage: a Continue lets every mount through, a Cancel is latched.</summary>
public class Wall3DNoticeGateTests
{
    [Fact]
    public async Task ACancel_IsLatched_SoALiveRebuildWhileTheNoticeIsOpenDoesNotAskAgain()
    {
        var gate = new Wall3DNoticeGate();
        var asked = 0;
        Task<Wall3DDataGate?> Ask()
        {
            asked++;
            return Task.FromResult<Wall3DDataGate?>(new Wall3DDataGate(true, 1000));
        }

        var first = gate.PassAsync(Ask, () => Task.CompletedTask);
        var rebuild = gate.PassAsync(Ask, () => Task.CompletedTask); // a new view arrived while the notice is up
        gate.Answer(false);

        Assert.False(await first);
        Assert.False(await rebuild);
        Assert.False(await gate.PassAsync(Ask, () => Task.CompletedTask));
        Assert.True(gate.Declined);
        Assert.Equal(1, asked);

        gate.Load();
        Assert.True(await gate.PassAsync(Ask, () => Task.CompletedTask));
        Assert.Equal(1, asked);
    }

    [Fact]
    public async Task AContinue_OrNothingToAsk_LetsEveryLaterMountThrough()
    {
        var gate = new Wall3DNoticeGate();
        var opened = gate.PassAsync(() => Task.FromResult<Wall3DDataGate?>(new Wall3DDataGate(true, null)), () => Task.CompletedTask);
        Assert.NotNull(gate.Open);
        gate.Answer(true);

        Assert.True(await opened);
        Assert.True(await gate.PassAsync(() => throw new InvalidOperationException("not asked again"), () => Task.CompletedTask));

        var quiet = new Wall3DNoticeGate();
        Assert.True(await quiet.PassAsync(() => Task.FromResult<Wall3DDataGate?>(null), () => Task.CompletedTask));
        Assert.Null(quiet.Open);
    }

    [Fact]
    public async Task AStageThatGoesAway_EndsTheWaitWithoutMounting()
    {
        var gate = new Wall3DNoticeGate();
        var waiting = gate.PassAsync(() => Task.FromResult<Wall3DDataGate?>(new Wall3DDataGate(true, 5)), () => Task.CompletedTask);

        gate.Abandon();

        Assert.False(await waiting);
        Assert.Null(gate.Open);
    }
}
