// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Security.Claims;
using Blocwerk.Core.Capture.Scan;
using Blocwerk.Core.Tests.Refresh;
using Blocwerk.Web.Endpoints;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Blocwerk.Core.Tests.Scan;

/// <summary>
/// A begin settles who may upload before it reads the manifest, stops reading at the manifest limit even without a
/// Content-Length, and runs once at a time per user, so nobody can hold many manifests in memory.
/// </summary>
public class WallScanBeginTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    [Fact]
    public async Task ACallerWhoMayNotUpload_IsRefused_BeforeTheManifestIsRead()
    {
        var scans = Substitute.For<IWallScanService>();
        scans.EnsureMayBeginAsync(default, default).ThrowsAsyncForAnyArgs(new UnauthorizedAccessException());
        var http = Json(new UnreadableStream());

        var result = await WallScanEndpoints.BeginAsync(Guid.NewGuid(), http, scans, None);

        Assert.Equal(StatusCodes.Status403Forbidden, Assert.IsType<JsonHttpResult<ScanErrorResponse>>(result).StatusCode);
        await scans.DidNotReceiveWithAnyArgs().BeginAsync(default, default!, default);
    }

    [Fact]
    public async Task AManifestOverTheLimitWithoutAContentLength_Is413()
    {
        var scans = Substitute.For<IWallScanService>();
        var body = new byte[ScanPackageLimits.MaxManifestBytes + 1];
        Array.Fill(body, (byte)' ');

        var result = await WallScanEndpoints.BeginAsync(Guid.NewGuid(), Json(new MemoryStream(body)), scans, None);

        Assert.Equal(StatusCodes.Status413PayloadTooLarge, Assert.IsType<JsonHttpResult<ScanErrorResponse>>(result).StatusCode);
        await scans.DidNotReceiveWithAnyArgs().BeginAsync(default, default!, default);
    }

    [Fact]
    public async Task OneBeginPerUser_IsInFlight_OverAllTheirKeys()
    {
        using var gate = new WallScanBeginGate();
        var release = new TaskCompletionSource<object?>();
        var first = gate.InvokeAsync(Context("gh|1", "key-a"), _ => new ValueTask<object?>(release.Task));

        var second = await gate.InvokeAsync(Context("gh|1", "key-b"), _ => ValueTask.FromResult<object?>("ran"));
        var other = await gate.InvokeAsync(Context("gh|2", "key-a"), _ => ValueTask.FromResult<object?>("ran"));
        release.SetResult("ran");
        await first;
        var after = await gate.InvokeAsync(Context("gh|1", "key-b"), _ => ValueTask.FromResult<object?>("ran"));

        Assert.Equal(StatusCodes.Status429TooManyRequests, Assert.IsType<JsonHttpResult<ScanErrorResponse>>(second).StatusCode);
        Assert.Equal("ran", other);
        Assert.Equal("ran", after);
    }

    private static DefaultHttpContext Json(Stream body)
    {
        var http = new DefaultHttpContext();
        http.Request.ContentType = "application/json";
        http.Request.Body = body;
        return http;
    }

    private static DefaultEndpointFilterInvocationContext Context(string user, string key)
    {
        var http = new DefaultHttpContext();
        http.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, user), new Claim(Authentication.Authorization.ApiKeyClaimTypes.ApiKeyId, key)], "test"));
        return new DefaultEndpointFilterInvocationContext(http);
    }
}
