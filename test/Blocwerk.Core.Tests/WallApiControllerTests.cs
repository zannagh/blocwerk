using System.Security.Claims;
using System.Text.Json;
using Blocwerk.Authentication.Authorization;
using Blocwerk.Authentication.Handlers;
using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Blocwerk.Web.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The wall-scoped machine API. Two things carry real risk here and are covered directly: the
/// unusual bare-number temperature body the gym's Raspberry Pi posts (its firmware cannot be
/// changed), and the wall-id check that keeps a key issued for one wall away from another.
/// </summary>
public class WallApiControllerTests
{
    private static readonly Guid WallA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid WallB = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Fact]
    public async Task Record_BareJsonNumber_IsStoredAsTheTemperature()
    {
        var service = Substitute.For<IWallTemperatureService>();
        var controller = TemperatureController(service, WallA);

        var result = await controller.Record(WallA, Body("24.3"), CancellationToken.None);

        Assert.IsType<NoContentResult>(result);
        await service.Received(1).RecordReadingAsync(WallA, 24.3d, null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Record_ObjectBody_IsStoredAsTheTemperature()
    {
        var service = Substitute.For<IWallTemperatureService>();
        var controller = TemperatureController(service, WallA);

        var body = Body("""{"temperatureCelsius": -4.5, "recordedAt": "2026-08-21T10:00:00Z"}""");
        var result = await controller.Record(WallA, body, CancellationToken.None);

        Assert.IsType<NoContentResult>(result);

        // 'recordedAt' is passed through rather than dropped: accepting it and storing something
        // else would be a lie the caller cannot see.
        await service.Received(1).RecordReadingAsync(
            WallA,
            -4.5d,
            new DateTimeOffset(2026, 8, 21, 10, 0, 0, TimeSpan.Zero),
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("120.5")]
    [InlineData("-273.15")]
    [InlineData("\"warm\"")]
    [InlineData("{\"other\": 1}")]
    public async Task Record_ImplausibleOrUnreadableBody_IsRejected(string json)
    {
        var service = Substitute.For<IWallTemperatureService>();
        var controller = TemperatureController(service, WallA);

        var result = await controller.Record(WallA, Body(json), CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
        await service.DidNotReceiveWithAnyArgs().RecordReadingAsync(default, default);
    }

    [Fact]
    public async Task Record_KeyForAnotherWall_IsForbidden()
    {
        var service = Substitute.For<IWallTemperatureService>();
        var controller = TemperatureController(service, WallA);

        var result = await controller.Record(WallB, Body("21.0"), CancellationToken.None);

        Assert.Equal(StatusCodes.Status403Forbidden, Assert.IsType<ObjectResult>(result).StatusCode);
        await service.DidNotReceiveWithAnyArgs().RecordReadingAsync(default, default);
    }

    [Fact]
    public async Task GetReadings_KeyForAnotherWall_IsForbidden()
    {
        var service = Substitute.For<IWallTemperatureService>();
        var controller = TemperatureController(service, WallA);

        var result = await controller.GetReadings(WallB, null, null, null, CancellationToken.None);

        Assert.Equal(StatusCodes.Status403Forbidden, Assert.IsType<ObjectResult>(result).StatusCode);
        await service.DidNotReceiveWithAnyArgs().GetReadingsAsync(default, default, default, default);
    }

    [Fact]
    public async Task GetReadings_WithoutARange_DefaultsToTheLastDay()
    {
        var service = Substitute.For<IWallTemperatureService>();
        service.GetReadingsAsync(WallA, Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new WallTemperaturePage(
                [new WallTemperatureReading { WallId = WallA, TemperatureCelsius = 18.5 }],
                false));
        var controller = TemperatureController(service, WallA);

        var result = await controller.GetReadings(WallA, null, null, null, CancellationToken.None);

        var readings = Assert.IsType<List<TemperatureReadingResponse>>(Assert.IsType<OkObjectResult>(result).Value);
        Assert.Equal(18.5, Assert.Single(readings).TemperatureCelsius);
        await service.Received(1).GetReadingsAsync(
            WallA,
            Arg.Is<DateTimeOffset>(f => Math.Abs((DateTimeOffset.UtcNow - f).TotalHours - 24) < 1),
            Arg.Any<DateTimeOffset>(),
            Arg.Any<int>(),
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(WallTemperatureService.MaxReadings + 1)]
    public async Task GetReadings_RejectsAnOutOfRangeSampleCap(int maxSamples)
    {
        var service = Substitute.For<IWallTemperatureService>();
        var controller = TemperatureController(service, WallA);

        // Rejected rather than silently clamped: the caller must know what it is getting.
        var result = await controller.GetReadings(WallA, null, null, maxSamples, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
        await service.DidNotReceiveWithAnyArgs().GetReadingsAsync(default, default, default, default);
    }

    [Fact]
    public async Task GetReadings_FlagsATruncatedSeriesToTheCaller()
    {
        var service = Substitute.For<IWallTemperatureService>();
        service.GetReadingsAsync(WallA, Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(), 5, Arg.Any<CancellationToken>())
            .Returns(new WallTemperaturePage(
                [new WallTemperatureReading { WallId = WallA, TemperatureCelsius = 18.5 }],
                true));
        var controller = TemperatureController(service, WallA);

        var result = await controller.GetReadings(WallA, null, null, 5, CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal("true", controller.Response.Headers["X-Blocwerk-Truncated"]);
    }

    [Fact]
    public async Task GetGallery_ReturnsEveryMergedSource()
    {
        var imageService = Substitute.For<IWallImageService>();
        var captured = new DateTimeOffset(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);
        imageService.GetGalleryAsync(WallA, 0, 50, Arg.Any<CancellationToken>()).Returns(
        [
            new WallGalleryItem(Guid.NewGuid(), WallGallerySource.Uploaded, WallA, "image/jpeg", 12, "camera", captured),
            new WallGalleryItem(WallA, WallGallerySource.WallPhoto, WallA, "image/png", 34, "Wall photo", captured.AddDays(-1)),
            new WallGalleryItem(Guid.NewGuid(), WallGallerySource.ResetPhoto, WallA, "image/webp", 56, "Reset", captured.AddDays(-2)),
        ]);

        var controller = ImagesController(imageService, WallA);
        var result = await controller.GetGallery(WallA, 0, 50, CancellationToken.None);

        var items = Assert.IsType<List<WallGalleryItemResponse>>(Assert.IsType<OkObjectResult>(result).Value);
        Assert.Equal(["Uploaded", "WallPhoto", "ResetPhoto"], items.Select(i => i.Source));
        Assert.Equal([12L, 34L, 56L], items.Select(i => i.SizeBytes));
    }

    [Fact]
    public async Task GetGallery_KeyForAnotherWall_IsForbidden()
    {
        var imageService = Substitute.For<IWallImageService>();
        var controller = ImagesController(imageService, WallA);

        var result = await controller.GetGallery(WallB, 0, 50, CancellationToken.None);

        Assert.Equal(StatusCodes.Status403Forbidden, Assert.IsType<ObjectResult>(result).StatusCode);
        await imageService.DidNotReceiveWithAnyArgs().GetGalleryAsync(default);
    }

    private static JsonElement Body(string json)
    {
        return JsonDocument.Parse(json).RootElement;
    }

    private static WallTemperatureController TemperatureController(IWallTemperatureService service, Guid keyWallId)
    {
        return Bind(new WallTemperatureController(service), keyWallId);
    }

    private static WallImagesController ImagesController(IWallImageService service, Guid keyWallId)
    {
        var controller = new WallImagesController(
            service,
            Substitute.For<IWallImageStorage>(),
            Substitute.For<ICurrentUserService>());
        return Bind(controller, keyWallId);
    }

    /// <summary>Puts a wall-scoped API-key principal on the controller, as the handler would.</summary>
    private static TController Bind<TController>(TController controller, Guid keyWallId)
        where TController : ControllerBase
    {
        var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.Name, "tester"),
                new Claim(ClaimTypes.NameIdentifier, "1"),
                new Claim(ApiKeyClaimTypes.Scope, ApiKeyScope.Wall.ToString()),
                new Claim(ApiKeyClaimTypes.ApiKeyId, Guid.NewGuid().ToString()),
                new Claim(ApiKeyClaimTypes.WallId, keyWallId.ToString()),
            ],
            ApiKeyAuthenticationHandler.SchemeName);

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) },
        };

        return controller;
    }
}
