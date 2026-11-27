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
/// and installation keys are out, see <see cref="PersonalApiKeyRouteTests"/>), answer the service's refusals with their
/// status, and take begin and commit as JSON only.
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

        Assert.Equal(5, endpoints.Count);
        foreach (var endpoint in endpoints)
        {
            var authorize = endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>();
            Assert.Contains(authorize, a => a.Policy == BlocwerkPolicies.HumanOrUserApiKey);
            Assert.DoesNotContain(authorize, a => a.Policy is null);
            Assert.Null(endpoint.Metadata.GetMetadata<EnableRateLimitingAttribute>()); // per user by WallScanRateLimiter instead
            Assert.StartsWith(WallScanEndpoints.Prefix, endpoint.RoutePattern.RawText, StringComparison.Ordinal);
        }

        Assert.True(ApiKeySurface.Covers(new PathString($"/api/walls/{Guid.NewGuid()}/scans/{Guid.NewGuid()}/files/video/seg0-000.mov")));
    }

    [Fact]
    public async Task ACallerWhoMayNotUpload_Gets403_NotARedirect()
    {
        var denied = await WallScanEndpoints.RunAsync<ScanUploadState>(() => throw new UnauthorizedAccessException());
        var kiosk = await WallScanEndpoints.RunAsync<ScanUploadState>(() => throw new KioskRestrictedException("no"));

        Assert.Equal(StatusCodes.Status403Forbidden, Assert.IsType<JsonHttpResult<ScanErrorResponse>>(denied).StatusCode);
        Assert.Equal(StatusCodes.Status403Forbidden, Assert.IsType<JsonHttpResult<ScanErrorResponse>>(kiosk).StatusCode);
    }

    [Fact]
    public async Task ARefusal_AnswersWithItsStatusAndProblems()
    {
        var result = await WallScanEndpoints.RunAsync<ScanUploadState>(
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

    [Fact]
    public async Task BeginAndCommit_WithoutAJsonContentType_Are415()
    {
        var scans = Substitute.For<IWallScanService>();
        var form = new DefaultHttpContext();
        form.Request.ContentType = "application/x-www-form-urlencoded";
        form.Request.Body = new MemoryStream("{}"u8.ToArray());
        var bare = new DefaultHttpContext();

        var begin = await WallScanEndpoints.BeginAsync(Guid.NewGuid(), form, scans, CancellationToken.None);
        var commit = await WallScanEndpoints.CommitAsync(Guid.NewGuid(), Guid.NewGuid(), bare, scans, CancellationToken.None);

        Assert.Equal(StatusCodes.Status415UnsupportedMediaType, Assert.IsType<JsonHttpResult<ScanErrorResponse>>(begin).StatusCode);
        Assert.Equal(StatusCodes.Status415UnsupportedMediaType, Assert.IsType<JsonHttpResult<ScanErrorResponse>>(commit).StatusCode);
        await scans.DidNotReceiveWithAnyArgs().BeginAsync(default, default!, default);
        await scans.DidNotReceiveWithAnyArgs().CommitAsync(default, default, default);
    }

    [Fact]
    public async Task ACommitWithAJsonContentType_ReachesTheService()
    {
        var scans = Substitute.For<IWallScanService>();
        var http = new DefaultHttpContext();
        http.Request.ContentType = "application/json; charset=utf-8";

        await WallScanEndpoints.CommitAsync(Guid.NewGuid(), Guid.NewGuid(), http, scans, CancellationToken.None);

        await scans.ReceivedWithAnyArgs(1).CommitAsync(default, default, default);
    }
}
