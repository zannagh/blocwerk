using Blocwerk.Authentication;
using Blocwerk.Core.Configuration;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Blocwerk.Core.Tests;

/// <summary>
/// A signed-out human asking for an [Authorize] Blazor page must be redirected to the login page.
/// Naming authentication schemes on the default policy broke exactly this in production: the
/// authorization middleware then challenges every named scheme, and the JWT handler's bare 401
/// won over the cookie handler's redirect. These tests pin both halves of the fix.
/// </summary>
public class AuthenticationChallengeTests
{
    /// <summary>
    /// The policy must name no schemes, because that is what sends the challenge to the app's
    /// DefaultChallengeScheme (the BlocwerkPolicy forwarder) instead of to cookie AND jwt.
    /// </summary>
    [Fact]
    public void HumanPolicy_NamesNoAuthenticationSchemes()
    {
        var policy = AuthenticationServices.BuildHumanPolicy(new AuthorizationPolicyBuilder());

        Assert.Empty(policy.AuthenticationSchemes);
    }

    /// <summary>Same reasoning for the gallery byte route's policy.</summary>
    [Fact]
    public void GalleryImagePolicy_NamesNoAuthenticationSchemes()
    {
        var policy = AuthenticationServices.BuildGalleryImagePolicy(new AuthorizationPolicyBuilder());

        Assert.Empty(policy.AuthenticationSchemes);
    }

    /// <summary>
    /// And this is what "no schemes named" buys: challenging the default scheme on a Blazor path
    /// answers 302 /account/login, not the bare 401 the JWT handler writes.
    /// </summary>
    [Theory]
    [InlineData("/walls")]
    [InlineData("/profile")]
    [InlineData("/media/walls/8f1c2f6e-0000-0000-0000-000000000000/gallery/uploaded/8f1c2f6e-0000-0000-0000-000000000001")]
    public async Task DefaultChallenge_RedirectsASignedOutHumanToTheLoginPage(string path)
    {
        await using var services = BuildAuthenticationServices();
        await using var scope = services.CreateAsyncScope();
        var context = HttpContextFor(path, scope.ServiceProvider);

        // No scheme argument: exactly what the authorization middleware does for a policy that
        // names none.
        await context.ChallengeAsync();

        Assert.Equal(StatusCodes.Status302Found, context.Response.StatusCode);
        var location = context.Response.Headers.Location.ToString();
        Assert.Contains("/account/login", location, StringComparison.Ordinal);
    }

    /// <summary>
    /// The counterpart: the JWT handler is what a scheme-naming policy would have challenged, and
    /// it answers 401 with no body. Keeps the test above honest about what it is proving.
    /// </summary>
    [Fact]
    public async Task ChallengingTheJwtSchemeExplicitly_StillProducesTheBare401()
    {
        await using var services = BuildAuthenticationServices();
        await using var scope = services.CreateAsyncScope();
        var context = HttpContextFor("/walls", scope.ServiceProvider);

        await context.ChallengeAsync(Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerDefaults.AuthenticationScheme);

        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
        Assert.True(string.IsNullOrEmpty(context.Response.Headers.Location.ToString()));
    }

    private static DefaultHttpContext HttpContextFor(string path, IServiceProvider services)
    {
        var context = new DefaultHttpContext { RequestServices = services };
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("blocwerk.example");
        context.Request.Path = path;
        context.Response.Body = new MemoryStream();
        return context;
    }

    /// <summary>
    /// The real registration, so the policy scheme, its forwarding selector and the cookie login
    /// path are the production ones. Development keeps the data-protection keys local instead of
    /// writing to /app/keys.
    /// </summary>
    private static ServiceProvider BuildAuthenticationServices()
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            EnvironmentName = Environments.Development,
        });

        builder.ConfigureAuthenticationAndAuthorization(new BlocwerkSettings(new ConfigurationBuilder().Build()));

        return builder.Services.BuildServiceProvider();
    }
}
