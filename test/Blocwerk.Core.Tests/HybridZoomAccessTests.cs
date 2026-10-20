// <copyright file="HybridZoomAccessTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services.HybridZoom;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using static Blocwerk.Core.Tests.HybridZoomScene;

namespace Blocwerk.Core.Tests;

/// <summary>
/// Who gets the hybrid zoom's close-ups: cut from capture photos (otherwise wall-admin only), they are for signed-in
/// members by default; share-token viewers and the kiosk only with AllowShareAndKiosk; nobody while the flag is off.
/// </summary>
public class HybridZoomAccessTests
{
    private const string Share = "share-me";

    public enum Caller
    {
        Admin,
        Member,
        NonMember,
        Anonymous,
        Kiosk,
        ShareToken,
        MemberWithStaleToken,
        AnonymousWithTokenOfAPrivateWall,
    }

    [Theory]
    [InlineData(Caller.Admin, true, true)]
    [InlineData(Caller.Member, true, true)]
    [InlineData(Caller.MemberWithStaleToken, true, true)]
    [InlineData(Caller.NonMember, false, false)]
    [InlineData(Caller.Anonymous, false, false)]
    [InlineData(Caller.Kiosk, false, true)]
    [InlineData(Caller.ShareToken, false, true)]
    [InlineData(Caller.AnonymousWithTokenOfAPrivateWall, false, false)]
    public async Task CloseUp_IsMembersOnlyByDefault_AndTheGalleryRuleWithAllowShareAndKiosk(Caller caller, bool byDefault, bool widened)
    {
        foreach (var (options, allowed) in new[]
        {
            (new HybridZoomOptions { Enabled = false }, false),
            (new HybridZoomOptions { Enabled = false, AllowShareAndKiosk = true }, false),
            (new HybridZoomOptions { Enabled = true }, byDefault),
            (new HybridZoomOptions { Enabled = true, AllowShareAndKiosk = true }, widened),
        })
        {
            using var h = new WallTestHarness();
            using var s = new CaptureScenario(h);
            var panelId = await HybridZoomServiceTests.SeedAsync(h, s, TimeSpan.FromHours(1));
            var (kiosk, token) = await ArrangeAsync(h, caller);

            var (result, _) = await HybridZoomServiceTests.ServeAsync(
                h, s, panelId, h.WallId, options, kiosk, token, HybridZoomServiceTests.Query((View.X, View.Y, View.W, View.H)));

            var label = $"{caller}, enabled {options.Enabled}, wide {options.AllowShareAndKiosk}";
            if (allowed)
            {
                Assert.True(result is FileContentHttpResult, $"{label}: {result.GetType().Name}");
            }
            else
            {
                Assert.True(result is NotFound, $"{label}: {result.GetType().Name}");
            }
        }
    }

    [Fact]
    public async Task FlagOff_IsNotFound_EvenWithoutAQuery_WhileOnAMissingRectangleIsABadRequest()
    {
        using var h = new WallTestHarness();
        using var s = new CaptureScenario(h);
        var panelId = await HybridZoomServiceTests.SeedAsync(h, s, TimeSpan.FromHours(1));
        var kiosk = Substitute.For<IKioskContext>();

        var (off, _) = await HybridZoomServiceTests.ServeAsync(h, s, panelId, h.WallId, new HybridZoomOptions(), kiosk, null, "x=abc");
        var (on, _) = await HybridZoomServiceTests.ServeAsync(h, s, panelId, h.WallId, new HybridZoomOptions { Enabled = true }, kiosk, null, "x=abc");

        Assert.IsType<NotFound>(off);
        Assert.IsType<BadRequest>(on);
    }

    private static async Task<(IKioskContext Kiosk, string? Token)> ArrangeAsync(WallTestHarness h, Caller caller)
    {
        var kiosk = Substitute.For<IKioskContext>();
        if (caller is not Caller.AnonymousWithTokenOfAPrivateWall)
        {
            await using var db = h.CreateContext();
            (await db.Walls.SingleAsync()).ShareToken = Share;
            await db.SaveChangesAsync();
        }

        switch (caller)
        {
            case Caller.Member:
                h.ActingUser = await h.AddMemberAsync("member@test", WallRole.Member);
                return (kiosk, null);
            case Caller.MemberWithStaleToken:
                h.ActingUser = await h.AddMemberAsync("member@test", WallRole.Member);
                return (kiosk, "an-old-token");
            case Caller.NonMember:
                h.ActingUser = await StrangerAsync(h);
                return (kiosk, null);
            case Caller.Admin:
                return (kiosk, null);
            default:
                break;
        }

        h.CurrentUser.GetCurrentUserAsync().Returns(_ => Task.FromException<User>(new UnauthorizedAccessException()));
        if (caller == Caller.Kiosk)
        {
            kiosk.IsKiosk.Returns(true);
            kiosk.KioskWallId.Returns(h.WallId);
        }

        return (kiosk, caller is Caller.ShareToken or Caller.AnonymousWithTokenOfAPrivateWall ? Share : null);
    }

    private static async Task<User> StrangerAsync(WallTestHarness h)
    {
        await using var db = h.CreateContext();
        db.Users.Add(new User { Identifier = "stranger@test", DisplayName = "Stranger" });
        await db.SaveChangesAsync();
        return await db.Users.SingleAsync(u => u.Identifier == "stranger@test");
    }
}
