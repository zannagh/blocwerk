// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Text.Json;
using Blocwerk.Authentication.Authorization;
using Blocwerk.Contracts.Feed;
using Blocwerk.Web.Endpoints.Feed;
using Microsoft.AspNetCore.Http;
using NSubstitute;

namespace Blocwerk.Core.Tests;

/// <summary>
/// A device key revoked while its feed stream is open: the stream's timed access check ends it although no feed row
/// arrives, and the reconnect is refused.
/// </summary>
public sealed class DeviceKeyStreamRevocationTests
{
    [Fact]
    public async Task ARevokedDeviceKey_IsCutOffByTheTimedCheck_AndItsReconnectIsRefused()
    {
        await using var f = new FeedRouteFixture(options: new FeedStreamOptions
        {
            Lifetime = TimeSpan.FromSeconds(30),
            AccessRecheck = TimeSpan.FromMilliseconds(200),
        });
        await f.StartAsync();
        await f.SeedAudiencesAsync();
        f.Principal = DeviceKeyTestPrincipals.Device();
        var keyId = f.Principal.GetApiKeyId()!.Value;
        await using var run = FeedStreamRun.Open(f, "?after=" + await f.BeginningAsync());
        await run.Body.WaitUntilAsync(b => b.Events.Count == 4);

        f.DeviceKeys.IsLiveAsync(keyId, Arg.Any<CancellationToken>()).Returns(false);
        await run.EndedAsync();

        var bye = run.Body.Frames[^1];
        Assert.Equal(FeedStreamEvents.Bye, bye.Event);
        Assert.Equal(FeedStreamEvents.ByeAccess, JsonSerializer.Deserialize<FeedStreamBye>(bye.Data!, FeedJson.Options)!.Reason);
        Assert.Equal(4, run.Body.Events.Count);

        var reconnect = await f.GetAsync(FeedEndpoints.StreamRoute, f.WallId, "?after=" + await f.BeginningAsync());
        FeedRouteFixture.AssertRefused(reconnect, StatusCodes.Status403Forbidden, FeedRefusals.NoAccess);
    }
}
