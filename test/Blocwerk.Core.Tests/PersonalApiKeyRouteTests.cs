using System.Security.Claims;
using Blocwerk.Authentication.Authorization;
using Blocwerk.Authentication.Handlers;
using Blocwerk.Core.Capture;
using Blocwerk.Core.Enums;
using Blocwerk.Web.Endpoints;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The capture video upload admits a signed-in human or a PERSONAL key acting as its owner, and no
/// other key: the upload's own gates (wall admin, not a kiosk, own open draft) then treat the key's
/// owner exactly as they treat the browser user.
/// </summary>
public class PersonalApiKeyRouteTests
{
    private const string Key = "Bearer bwk_0123456789abcdef0123456789abcdef";

    [Fact]
    public async Task HumanOrUserApiKeyPolicy_AdmitsHumansAndPersonalKeys_Only()
    {
        var authorization = new ServiceCollection().AddLogging().AddAuthorization()
            .BuildServiceProvider().GetRequiredService<IAuthorizationService>();
        var policy = HumanOrPersonalApiKeyPolicy.Build(new AuthorizationPolicyBuilder());

        async Task<bool> Admits(ClaimsPrincipal principal) =>
            (await authorization.AuthorizeAsync(principal, resource: null, policy)).Succeeded;

        Assert.True(await Admits(CookiePrincipal()));
        Assert.True(await Admits(ApiKeyPrincipal(ApiKeyScope.User, allowWrite: true)));

        // A personal key its owner did not allow to change walls stays out.
        Assert.False(await Admits(ApiKeyPrincipal(ApiKeyScope.User)));

        Assert.False(await Admits(new ClaimsPrincipal(new ClaimsIdentity())));
        Assert.False(await Admits(ApiKeyPrincipal(ApiKeyScope.Wall, Guid.NewGuid())));
        Assert.False(await Admits(ApiKeyPrincipal(ApiKeyScope.Kiosk, Guid.NewGuid())));
        Assert.False(await Admits(ApiKeyPrincipal(ApiKeyScope.Installation)));

        // A "user" key that somehow carries a wall is not a personal key.
        Assert.False(await Admits(ApiKeyPrincipal(ApiKeyScope.User, Guid.NewGuid(), allowWrite: true)));

        // A write claim on any other scope grants nothing.
        Assert.False(await Admits(ApiKeyPrincipal(ApiKeyScope.Installation, allowWrite: true)));
    }

    [Theory]
    [InlineData("/api/captures/8f1c2f6e-0000-0000-0000-000000000000/video", true)]
    [InlineData("/API/Captures/8f1c2f6e-0000-0000-0000-000000000000/video", true)]
    [InlineData("/api/capturesque", false)]
    [InlineData("/captures/8f1c2f6e-0000-0000-0000-000000000000/video", false)]
    public void SelectScheme_ForwardsAKeyOnTheCaptureApi(string path, bool covered)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Request.Headers.Authorization = Key;

        var expected = covered ? ApiKeyAuthenticationHandler.SchemeName : CookieAuthenticationDefaults.AuthenticationScheme;
        Assert.Equal(expected, ApiKeySurface.SelectScheme(context));
    }

    [Fact]
    public void CaptureVideoUpload_DeclaresTheHumanOrPersonalKeyPolicy()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddAuthorization();
        builder.Services.AddSingleton(Substitute.For<IWallCaptureService>());
        builder.Services.AddSingleton(new WallCapturePipelineOptions());
        var app = builder.Build();
        app.MapCaptureVideoUpload();

        var endpoint = Assert.Single(((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText == "/api/captures/{captureId:guid}/video"));

        var authorize = endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>();
        Assert.Contains(authorize, a => a.Policy == BlocwerkPolicies.HumanOrUserApiKey);
        Assert.DoesNotContain(authorize, a => a.Policy is null);
    }

    private static ClaimsPrincipal ApiKeyPrincipal(ApiKeyScope scope, Guid? wallId = null, bool allowWrite = false)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, "Some Climber"),
            new(ClaimTypes.NameIdentifier, "gh|4711"),
            new(ApiKeyClaimTypes.Scope, scope.ToString()),
            new(ApiKeyClaimTypes.ApiKeyId, Guid.NewGuid().ToString()),
        };

        if (allowWrite)
        {
            claims.Add(new Claim(ApiKeyClaimTypes.AllowWrite, "true"));
        }

        if (wallId is { } id)
        {
            claims.Add(new Claim(ApiKeyClaimTypes.WallId, id.ToString()));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, ApiKeyAuthenticationHandler.SchemeName));
    }

    private static ClaimsPrincipal CookiePrincipal()
    {
        return new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, "Some Climber"), new Claim(ClaimTypes.NameIdentifier, "gh|4711")],
            CookieAuthenticationDefaults.AuthenticationScheme));
    }
}
