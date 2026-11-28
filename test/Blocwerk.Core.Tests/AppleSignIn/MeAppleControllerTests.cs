// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Entities;
using Blocwerk.Web.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Blocwerk.Core.Tests.AppleSignIn;

/// <summary>
/// <c>/api/v1/me/apple</c> with a device key: link (204), a bad token (400), an Apple ID on another account (409),
/// unlink (204), and the profile's <c>appleLinked</c> following along.
/// </summary>
public sealed class MeAppleControllerTests
{
    [Fact]
    public async Task Link_ThenUnlink_AndTheProfileFollows()
    {
        using var bed = AppleSignInTestBed.Create();
        var user = await bed.AddUserAsync("climber");
        var controller = Controller(bed, user);

        var linked = await controller.Link(await Body(bed, "apple-sub-1"), CancellationToken.None);

        Assert.IsType<NoContentResult>(linked);
        Assert.True(await ProfileLinkedAsync(bed, user));

        Assert.IsType<NoContentResult>(await controller.Unlink(CancellationToken.None));
        Assert.False(await ProfileLinkedAsync(bed, user));
        Assert.IsType<NoContentResult>(await controller.Unlink(CancellationToken.None));
    }

    [Fact]
    public async Task AnAppleIdOnAnotherAccount_Answers409()
    {
        using var bed = AppleSignInTestBed.Create();
        var first = await bed.AddUserAsync("first");
        var second = await bed.AddUserAsync("second");
        await bed.LinkAsync(first.Id, "apple-sub-1");

        var result = await Controller(bed, second).Link(await Body(bed, "apple-sub-1"), CancellationToken.None);

        var conflict = Assert.IsType<ConflictObjectResult>(result);
        Assert.Equal(MeAppleController.LinkedElsewhereMessage, Assert.IsType<ApiErrorResponse>(conflict.Value).Message);
    }

    [Fact]
    public async Task ABadTokenOrAReusedNonce_Answers400()
    {
        using var bed = AppleSignInTestBed.Create();
        var user = await bed.AddUserAsync("climber");
        var controller = Controller(bed, user);
        var body = await Body(bed, "apple-sub-1");

        Assert.IsType<BadRequestObjectResult>(await controller.Link(body with { IdentityToken = "forged" }, CancellationToken.None));
        Assert.IsType<BadRequestObjectResult>(await controller.Link(body, CancellationToken.None));
    }

    private static async Task<AppleLinkRequest> Body(AppleSignInTestBed bed, string subject) =>
        new(StubAppleValidator.Token(subject), await bed.Nonces.IssueAsync());

    private static async Task<bool> ProfileLinkedAsync(AppleSignInTestBed bed, User user)
    {
        var profile = new MeProfileController(MeProfileControllerTests.RealCurrentUser(bed.Harness, user), bed.Service)
        {
            ControllerContext = Context(),
        };
        var ok = Assert.IsType<OkObjectResult>(await profile.Get());
        return Assert.IsType<MeProfileResponse>(ok.Value).AppleLinked;
    }

    private static MeAppleController Controller(AppleSignInTestBed bed, User user) =>
        new(MeProfileControllerTests.RealCurrentUser(bed.Harness, user), bed.Service) { ControllerContext = Context() };

    private static ControllerContext Context() =>
        new() { HttpContext = new DefaultHttpContext { User = DeviceKeyTestPrincipals.Device() } };
}
