// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Client.ThreeD;
using Microsoft.AspNetCore.Components;

namespace Blocwerk.Core.Tests.OfflineClient3D;

/// <summary>
/// One 3D stage's life: a ready status mounts once, with that status's document; nothing else mounts (so no 3D code
/// loads for a wall the device holds no 3D of, has opted out of, or holds only in part); a model gone meanwhile or a
/// failed start shows as such; another wall unmounts first and reads again; no label reads nothing.
/// </summary>
public sealed class View3DSessionTests
{
    private const string Partition = "u-aaaaaaaaaaaaaaaaaaaa";
    private static readonly Guid Wall = Guid.Parse("11111111-1111-1111-1111-111111111111");

    [Fact]
    public async Task AReadyStatus_MountsOnce_WithItsDocument()
    {
        var client = new FakeClient3D { Status = new("ready", new string('d', 64), true) };
        var session = new View3DSession(client);

        await session.LoadAsync("k1", Partition, Wall);
        Assert.True(session.ShowsStage);
        Assert.False(await session.MountAsync(default, Request));
        Assert.False(await session.MountAsync(default, Request));

        Assert.Equal(new string('d', 64), Assert.Single(client.Mounts).Sha);
        Assert.Equal([(Partition, Wall)], client.StatusReads);
    }

    [Theory]
    [InlineData("none")]
    [InlineData("off")]
    [InlineData("missing")]
    [InlineData("ready")]
    public async Task NothingButAReadyStatusWithADocument_EverMounts(string state)
    {
        var client = new FakeClient3D { Status = new(state, null, false) };
        var session = new View3DSession(client);

        await session.LoadAsync("k1", Partition, Wall);

        Assert.False(session.ShowsStage);
        Assert.False(await session.MountAsync(default, Request));
        Assert.Empty(client.Mounts);
    }

    [Fact]
    public async Task AModelGoneBeforeTheMount_ShowsAsMissing()
    {
        var client = new FakeClient3D { Status = new("ready", new string('d', 64), false), MountAnswer = "missing" };
        var session = new View3DSession(client);
        await session.LoadAsync("k1", Partition, Wall);

        Assert.True(await session.MountAsync(default, Request));
        Assert.Equal(Client3DState.Missing, session.Status!.Kind);
        Assert.False(session.ShowsStage);
    }

    [Theory]
    [InlineData("model", "could not be drawn")]
    [InlineData("webgl", "WebGL")]
    [InlineData("unavailable", "could not be loaded")]
    public async Task AFailedStart_SaysWhy(string answer, string text)
    {
        var client = new FakeClient3D { Status = new("ready", new string('d', 64), false), MountAnswer = answer };
        var session = new View3DSession(client);
        await session.LoadAsync("k1", Partition, Wall);

        Assert.True(await session.MountAsync(default, Request));
        Assert.Contains(text, session.Failure, StringComparison.Ordinal);
        Assert.False(session.ShowsStage);
    }

    [Fact]
    public async Task AnotherWall_UnmountsTheViewer_AndReadsAgain_TheSameOneDoesNot()
    {
        var client = new FakeClient3D { Status = new("ready", new string('d', 64), false) };
        var session = new View3DSession(client);
        await session.LoadAsync("k1", Partition, Wall);
        await session.MountAsync(default(ElementReference), Request);

        await session.LoadAsync("k1", Partition, Wall);
        Assert.Equal(0, client.Unmounts);
        Assert.Single(client.StatusReads);

        await session.LoadAsync("k2", Partition, Wall);
        Assert.Equal(1, client.Unmounts);
        Assert.Equal(2, client.StatusReads.Count);
        await session.MountAsync(default, Request);
        Assert.Equal(2, client.Mounts.Count);

        await session.UnmountAsync();
        await session.UnmountAsync();
        Assert.Equal(2, client.Unmounts);
    }

    [Fact]
    public async Task NoLabel_ReadsNothing()
    {
        var client = new FakeClient3D { Status = new("ready", new string('d', 64), false) };
        var session = new View3DSession(client);

        await session.LoadAsync("k1", null, Wall);

        Assert.Equal(Client3DState.None, session.Status!.Kind);
        Assert.Empty(client.StatusReads);
    }

    private static Client3DRequest Request(Client3DStatus status) =>
        new(Partition, Wall.ToString(), status.Sha!, null, null, "schematic", new Dictionary<string, string>());
}
