// <copyright file="WallRefreshCheckCropEndpointTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Blocwerk.Authentication.Authorization;
using Blocwerk.Authentication.Handlers;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Refresh;
using Blocwerk.Core.Services;
using Blocwerk.Web.Endpoints;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Blocwerk.Core.Tests.Refresh;

/// <summary>
/// <c>GET /api/refreshes/{id}/checks/{checkId}/{view}</c> is for a signed-in person or a personal write key: kiosk,
/// wall and share-link callers never reach the service, and a kiosk device session is refused by the service's own check.
/// </summary>
public class WallRefreshCheckCropEndpointTests
{
    private const string Who = "X-Who";

    [Theory]
    [InlineData("kiosk-key", HttpStatusCode.Forbidden)]
    [InlineData("wall-key", HttpStatusCode.Forbidden)]
    [InlineData("read-only-personal-key", HttpStatusCode.Forbidden)]
    [InlineData("share-link", HttpStatusCode.Unauthorized)]
    [InlineData("anonymous", HttpStatusCode.Unauthorized)]
    public async Task APrincipalTheRouteDoesNotAdmit_NeverReachesTheService(string who, HttpStatusCode expected)
    {
        var refreshes = Substitute.For<IWallRefreshService>();
        await using var host = await StartAsync(refreshes);

        var response = await Get(host, who);

        Assert.Equal(expected, response.StatusCode);
        await refreshes.DidNotReceiveWithAnyArgs().GetCheckCropAsync(default, default, default, default);
    }

    [Fact]
    public async Task AKioskDeviceSession_IsRefusedByTheServicesOwnCheck()
    {
        var refreshes = Substitute.For<IWallRefreshService>();
        refreshes.GetCheckCropAsync(default, default, default, default)
            .ReturnsForAnyArgs(Task.FromException<byte[]?>(new KioskRestrictedException("kiosk")));
        await using var host = await StartAsync(refreshes);

        Assert.Equal(HttpStatusCode.Forbidden, (await Get(host, "kiosk-device")).StatusCode);
    }

    [Fact]
    public async Task APersonOrAPersonalWriteKey_GetsThePicture_AndAMissingOneIs404()
    {
        var refreshes = Substitute.For<IWallRefreshService>();
        refreshes.GetCheckCropAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), CheckCropView.Old, Arg.Any<CancellationToken>())
            .Returns(new byte[] { 0xFF, 0xD8, 1 });
        refreshes.GetCheckCropAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), CheckCropView.Model, Arg.Any<CancellationToken>())
            .Returns((byte[]?)null);
        await using var host = await StartAsync(refreshes);

        var person = await Get(host, "person", "old");
        var key = await Get(host, "personal-write-key", "old");
        var none = await Get(host, "person", "model");

        Assert.Equal(HttpStatusCode.OK, person.StatusCode);
        Assert.Equal("image/jpeg", person.Content.Headers.ContentType?.MediaType);
        Assert.Equal(HttpStatusCode.OK, key.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, none.StatusCode);
    }

    private static Task<HttpResponseMessage> Get(Host host, string who, string view = "old")
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/refreshes/{Guid.NewGuid()}/checks/{Guid.NewGuid()}/{view}");
        request.Headers.Add(Who, who);
        return host.Client.SendAsync(request);
    }

    private static async Task<Host> StartAsync(IWallRefreshService refreshes)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(refreshes);
        builder.Services.AddAuthentication("test").AddScheme<AuthenticationSchemeOptions, HeaderAuthHandler>("test", null);
        builder.Services.AddAuthorization(o => o.AddPolicy(
            BlocwerkPolicies.HumanOrUserApiKey, HumanOrPersonalApiKeyPolicy.Build(new AuthorizationPolicyBuilder())));
        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapWallRefreshCheckCrops();
        await app.StartAsync();
        return new Host(app);
    }

    private sealed class Host(WebApplication app) : IAsyncDisposable
    {
        public HttpClient Client { get; } = app.GetTestClient();

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await app.DisposeAsync();
        }
    }

    /// <summary>Builds the principal a request names in its header; none or "share-link" (no account) is anonymous.</summary>
    private sealed class HeaderAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var who = Request.Headers[Who].ToString();
            var claims = new List<Claim> { new(ClaimTypes.Name, "Someone"), new(ClaimTypes.NameIdentifier, "gh|1") };
            switch (who)
            {
                case "person":
                case "kiosk-device":
                    break;
                case "personal-write-key":
                    claims.AddRange(Key(ApiKeyScope.User, write: true));
                    break;
                case "read-only-personal-key":
                    claims.AddRange(Key(ApiKeyScope.User, write: false));
                    break;
                case "wall-key":
                    claims.AddRange(Key(ApiKeyScope.Wall, write: true, Guid.NewGuid()));
                    break;
                case "kiosk-key":
                    claims.AddRange(Key(ApiKeyScope.Kiosk, write: true, Guid.NewGuid()));
                    break;
                default:
                    return Task.FromResult(AuthenticateResult.NoResult());
            }

            // A key principal carries the API-key scheme's name as its authentication type, as the real handler gives it.
            var type = who.EndsWith("key", StringComparison.Ordinal) ? ApiKeyAuthenticationHandler.SchemeName : "test";
            var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, type));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, "test")));
        }

        private static IEnumerable<Claim> Key(ApiKeyScope scope, bool write, Guid? wall = null)
        {
            yield return new Claim(ApiKeyClaimTypes.Scope, scope.ToString());
            yield return new Claim(ApiKeyClaimTypes.ApiKeyId, Guid.NewGuid().ToString());
            if (write)
            {
                yield return new Claim(ApiKeyClaimTypes.AllowWrite, "true");
            }

            if (wall is { } id)
            {
                yield return new Claim(ApiKeyClaimTypes.WallId, id.ToString());
            }
        }
    }
}
