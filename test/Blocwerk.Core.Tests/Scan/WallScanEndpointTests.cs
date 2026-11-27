// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Authentication.Authorization;
using Blocwerk.Core.Capture.Scan;
using Blocwerk.Core.Services;
using Blocwerk.Core.Tests.Refresh;
using Blocwerk.Web.Endpoints;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Blocwerk.Core.Tests.Scan;

/// <summary>
/// The scan routes sit on the API-key surface, declare the human-or-personal-write-key policy (so wall keys, kiosk keys
/// and installation keys are out, see <see cref="PersonalApiKeyRouteTests"/>) and the rate limit, and answer the
/// service's refusals with their status.
/// </summary>
public class WallScanEndpointTests
{
    [Fact]
    public void EveryScanRoute_DeclaresThePolicyAndTheRateLimit_UnderTheApiKeySurface()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddAuthorization();
        builder.Services.AddSingleton(Substitute.For<IWallScanService>());
        var app = builder.Build();
        app.MapWallScans();

        var endpoints = ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints).OfType<RouteEndpoint>().ToList();

        Assert.Equal(4, endpoints.Count);
        foreach (var endpoint in endpoints)
        {
            var authorize = endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>();
            Assert.Contains(authorize, a => a.Policy == BlocwerkPolicies.HumanOrUserApiKey);
            Assert.DoesNotContain(authorize, a => a.Policy is null);
            Assert.Equal(WallScanRateLimit.Policy, endpoint.Metadata.GetMetadata<EnableRateLimitingAttribute>()?.PolicyName);
            Assert.StartsWith(WallScanEndpoints.Prefix, endpoint.RoutePattern.RawText, StringComparison.Ordinal);
        }

        Assert.True(ApiKeySurface.Covers(new PathString($"/api/walls/{Guid.NewGuid()}/scans/{Guid.NewGuid()}/files/video/seg0-000.mov")));
    }

    [Fact]
    public async Task ACallerWhoMayNotUpload_Gets403_NotARedirect()
    {
        var denied = await WallScanEndpoints.RunAsync(() => throw new UnauthorizedAccessException());
        var kiosk = await WallScanEndpoints.RunAsync(() => throw new KioskRestrictedException("no"));

        Assert.Equal(StatusCodes.Status403Forbidden, Assert.IsType<JsonHttpResult<ScanErrorResponse>>(denied).StatusCode);
        Assert.Equal(StatusCodes.Status403Forbidden, Assert.IsType<JsonHttpResult<ScanErrorResponse>>(kiosk).StatusCode);
    }

    [Fact]
    public async Task ARefusal_AnswersWithItsStatusAndProblems()
    {
        var result = await WallScanEndpoints.RunAsync(
            () => throw new ScanUploadException(ScanUploadException.BadRequest, "bad", ["frames[0].pose: wrong"]));

        var json = Assert.IsType<JsonHttpResult<ScanErrorResponse>>(result);
        Assert.Equal(StatusCodes.Status400BadRequest, json.StatusCode);
        Assert.Equal(["frames[0].pose: wrong"], json.Value!.Problems);
    }

    [Fact]
    public async Task AnOversizeManifest_IsRefusedBeforeItIsRead()
    {
        var scans = Substitute.For<IWallScanService>();
        var http = new DefaultHttpContext();
        http.Request.ContentLength = ScanPackageLimits.MaxManifestBytes + 1;
        http.Request.Body = new UnreadableStream();

        var result = await WallScanEndpoints.BeginAsync(Guid.NewGuid(), http, scans, CancellationToken.None);

        Assert.Equal(StatusCodes.Status413PayloadTooLarge, Assert.IsType<JsonHttpResult<ScanErrorResponse>>(result).StatusCode);
        await scans.DidNotReceiveWithAnyArgs().BeginAsync(default, default!, default);
    }
}
