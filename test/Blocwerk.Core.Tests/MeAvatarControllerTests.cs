// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Web.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SkiaSharp;

namespace Blocwerk.Core.Tests;

/// <summary>
/// <c>PUT</c>/<c>DELETE /api/v1/me/avatar</c>: the profile page's avatar pipeline (JPEG, PNG or WebP, scaled and stored
/// as WebP), and its refusals as 400.
/// </summary>
public sealed class MeAvatarControllerTests
{
    [Fact]
    public async Task APng_IsStoredAsTheAvatar_AndRemoved()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);

        var put = Assert.IsType<OkObjectResult>(await Controller(h, Png(), "image/png").Put(default));
        Assert.Equal($"/api/users/{h.Owner.Id}/avatar", Assert.IsType<MeProfileResponse>(put.Value).AvatarUrl);
        await using (var db = h.CreateContext())
        {
            Assert.Equal("image/webp", (await db.Users.SingleAsync(u => u.Id == h.Owner.Id)).AvatarContentType);
        }

        var removed = Assert.IsType<OkObjectResult>(await Controller(h, [], null).Delete());
        Assert.Null(Assert.IsType<MeProfileResponse>(removed.Value).AvatarUrl);
    }

    [Theory]
    [InlineData("image/gif")]
    [InlineData("image/jpeg")]
    [InlineData("image/webp")]
    [InlineData(null)]
    public async Task AnotherOrAWrongType_IsA400(string? contentType)
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);

        Assert.IsType<BadRequestObjectResult>(await Controller(h, Png(), contentType).Put(default));
    }

    [Fact]
    public async Task AnEmptyOrUnreadableBody_IsA400()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);

        Assert.IsType<BadRequestObjectResult>(await Controller(h, [], "image/png").Put(default));
        Assert.IsType<BadRequestObjectResult>(await Controller(h, [1, 2, 3, 4], "image/png").Put(default));
    }

    private static byte[] Png()
    {
        using var bitmap = new SKBitmap(8, 8);
        bitmap.Erase(SKColors.OrangeRed);
        using var image = SKImage.FromBitmap(bitmap);
        return image.Encode(SKEncodedImageFormat.Png, 100).ToArray();
    }

    private static MeAvatarController Controller(WallTestHarness h, byte[] body, string? contentType)
    {
        var http = new DefaultHttpContext { User = DeviceKeyTestPrincipals.Device() };
        http.Request.Body = new MemoryStream(body);
        http.Request.ContentType = contentType;
        return new MeAvatarController(MeProfileControllerTests.RealCurrentUser(h, h.Owner))
        {
            ControllerContext = new ControllerContext { HttpContext = http },
        };
    }
}
