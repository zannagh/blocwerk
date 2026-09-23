// <copyright file="UserFacingErrorMappingTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Security.Claims;
using Blocwerk.Authentication.Authorization;
using Blocwerk.Authentication.Handlers;
using Blocwerk.Core.Capture;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Blocwerk.Web.Controllers;
using Blocwerk.Web.Endpoints;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The shapes API and the capture video upload echo only <see cref="UserFacingException"/> messages; any
/// other <see cref="InvalidOperationException"/> (EF Core, framework internals) gets a generic text with
/// the same status code, so internals never leak to the caller.
/// </summary>
public class UserFacingErrorMappingTests
{
    private const string Internal = "The instance of entity type 'WallUpdateSession' cannot be tracked (secret detail).";
    private const string Intended = "The shape recognition is Running; finish or skip it before moving on.";

    [Fact]
    public async Task ShapesApi_UnexpectedInvalidOperation_ReturnsTheGenericMessage()
    {
        var result = await ShapesStatusThrowing(new InvalidOperationException(Internal));

        var conflict = Assert.IsType<ConflictObjectResult>(result);
        Assert.Equal(UserFacingException.GenericMessage, Assert.IsType<ApiErrorResponse>(conflict.Value).Message);
    }

    [Fact]
    public async Task ShapesApi_UserFacingException_KeepsItsMessage()
    {
        var result = await ShapesStatusThrowing(new UserFacingException(Intended));

        var conflict = Assert.IsType<ConflictObjectResult>(result);
        Assert.Equal(Intended, Assert.IsType<ApiErrorResponse>(conflict.Value).Message);
    }

    [Fact]
    public async Task ShapesApi_SupersededSession_StillExplainsItself()
    {
        var wallId = Guid.NewGuid();
        var result = await ShapesStatusThrowing(new WallUpdateSessionSupersededException(wallId, Guid.NewGuid(), null), wallId);

        var conflict = Assert.IsType<ConflictObjectResult>(result);
        Assert.StartsWith("This wall update was replaced", Assert.IsType<ApiErrorResponse>(conflict.Value).Message);
    }

    [Fact]
    public async Task VideoUpload_UnexpectedInvalidOperation_ReturnsTheGenericMessage()
    {
        var result = await UploadThrowing(new InvalidOperationException(Internal));

        Assert.Equal(UserFacingException.GenericMessage, Assert.IsType<BadRequest<string>>(result).Value);
    }

    [Fact]
    public async Task VideoUpload_UserFacingException_KeepsItsMessage()
    {
        var result = await UploadThrowing(new UserFacingException("Switch on printed markers for this wall first."));

        Assert.Equal("Switch on printed markers for this wall first.", Assert.IsType<BadRequest<string>>(result).Value);
    }

    private static async Task<IActionResult> ShapesStatusThrowing(Exception ex, Guid? wall = null)
    {
        var wallId = wall ?? Guid.NewGuid();
        var service = Substitute.For<IWallUpdateShapeService>();
        service.GetStatusAsync(wallId, Arg.Any<CancellationToken>()).ThrowsAsync(ex);
        var controller = new WallUpdateShapesController(service, NullLogger<WallUpdateShapesController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = WallKey(wallId) } },
        };
        return await controller.Status(wallId, default);
    }

    private static async Task<IResult> UploadThrowing(Exception ex)
    {
        var captures = Substitute.For<IWallCaptureService>();
        captures.AddVideoAsync(Arg.Any<Guid>(), Arg.Any<string?>(), Arg.Any<Stream>(), Arg.Any<CancellationToken>()).ThrowsAsync(ex);
        var http = new DefaultHttpContext();
        http.Request.Body = new MemoryStream([1, 2, 3]);
        return await CaptureVideoUploadEndpoint.HandleAsync(
            Guid.NewGuid(), "walk.mp4", http, captures, new WallCapturePipelineOptions(), NullLoggerFactory.Instance, default);
    }

    private static ClaimsPrincipal WallKey(Guid wallId) => new(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, "1"),
            new Claim(ApiKeyClaimTypes.Scope, ApiKeyScope.Wall.ToString()),
            new Claim(ApiKeyClaimTypes.ApiKeyId, Guid.NewGuid().ToString()),
            new Claim(ApiKeyClaimTypes.WallId, wallId.ToString()),
        ],
        ApiKeyAuthenticationHandler.SchemeName));
}
