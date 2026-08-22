using System.Text;
using Blocwerk.Authentication.Authorization;
using Blocwerk.Authentication.Handlers;
using Blocwerk.Authentication.Middleware;
using Blocwerk.Authentication.Providers;
using Blocwerk.Authentication.Services;
using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Configuration;
using Blocwerk.Core.Enums;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Tokens;

namespace Blocwerk.Authentication;

public static class AuthenticationServices
{
    public static IHostApplicationBuilder ConfigureAuthenticationAndAuthorization(this IHostApplicationBuilder app, BlocwerkSettings configuration)
    {
        app.Services.AddHttpClient();
        app.Services.AddHttpContextAccessor();
        app.Services.AddAuthenticationCore();
        app.Services.AddCascadingAuthenticationState();
        var dataProtection = app.Services.AddDataProtection().SetApplicationName("Blocwerk");
        if (!app.Environment.IsDevelopment())
        {
            // Persist the key ring to a mounted volume (docker-compose maps ./dpkeys -> /app/keys).
            // Without this the keys live in the container's ephemeral filesystem and are regenerated
            // on every redeploy, which invalidates every auth cookie AND the OAuth state/correlation
            // cookie: everyone is logged out on each deploy, and anyone mid-login when a deploy lands
            // hits "auth_failed". In Development we keep the default local key store.
            dataProtection.PersistKeysToFileSystem(new System.IO.DirectoryInfo("/app/keys"));
        }

        app.Services.AddSingleton<RedirectUriProvider>();
        app.Services.AddSingleton<CodeBasedAuthProvider>();
        app.Services.AddSingleton<ISecurityTokenHandler, JwtTokenHandler>();
        app.Services.AddSingleton<IRefreshTokenHandler, RefreshTokenHandler>();

        app.Services.AddScoped<AuthenticationStateProvider, CookieAuthenticationStateProvider>();
        app.Services.AddScoped<ICurrentUserService, CurrentUserService>();
        app.Services.AddSingleton<IAuthorizationHandler, WallGalleryImageHandler>();

        var policyScheme = "BlocwerkPolicy";

        app.Services.AddAuthentication(policyScheme)
            .AddPolicyScheme(policyScheme, policyScheme, options =>
            {
                // This policy scheme is the app's DEFAULT scheme, so whatever it forwards to
                // populates HttpContext.User on EVERY request. The selection — including the path
                // gate that keeps an API key off the browser's surface — lives in ApiKeySurface.
                options.ForwardDefaultSelector = ApiKeySurface.SelectScheme;
            })
            .AddJwtBearer(options =>
            {
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    LogValidationExceptions = true,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(configuration.JwtKey)),
                    ValidateIssuer = true,
                    ValidIssuer = configuration.Server.JwtIssuer,
                    ValidateAudience = false,
                };
            })
            .AddCookie(CookieAuthenticationDefaults.AuthenticationScheme, options =>
            {
                options.LoginPath = "/account/login";
                options.LogoutPath = "/account/logout";
                options.SlidingExpiration = true;
                options.ExpireTimeSpan = TimeSpan.FromHours(8);
            })
            .AddScheme<ApiKeyAuthenticationOptions, ApiKeyAuthenticationHandler>(
                ApiKeyAuthenticationHandler.SchemeName,
                _ => { });

        app.Services.AddAuthorization(options =>
        {
            // A bare [Authorize] must never be satisfiable by an API-key principal: it names the
            // human schemes explicitly, so the policy evaluator re-authenticates with those two
            // and an API-key identity simply is not part of the principal it judges. Cookie and
            // JWT callers are unaffected — they were already being authenticated by these schemes.
            options.DefaultPolicy = BuildHumanPolicy(new AuthorizationPolicyBuilder());

            // No FallbackPolicy on purpose: it would apply to every endpoint without authorization
            // metadata, which includes the login routes and the anonymous share-token routes.
            options.AddPolicy(BlocwerkPolicies.WallApiKey, policy => BuildApiKeyPolicy(policy, ApiKeyScope.Wall));
            options.AddPolicy(BlocwerkPolicies.UserApiKey, policy => BuildApiKeyPolicy(policy, ApiKeyScope.User));
            options.AddPolicy(BlocwerkPolicies.AnyApiKey, policy => BuildApiKeyPolicy(policy, null));
            options.AddPolicy(BlocwerkPolicies.WallGalleryImage, BuildGalleryImagePolicy(new AuthorizationPolicyBuilder()));
        });

        app.Services.AddAntiforgery(options =>
        {
            options.Cookie.SecurePolicy = app.Environment.IsDevelopment()
                ? CookieSecurePolicy.SameAsRequest
                : CookieSecurePolicy.Always;
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Strict;
        });

        return app;
    }

    public static WebApplication ConfigureAuthenticationMiddlewares(this WebApplication app)
    {
        app.UseAuthentication();

        // Development uses the REAL OAuth flow by default (the GitHub app allows
        // http://localhost:5050/oauth-callback). The dev auto-login bypass only kicks in when you
        // opt into it by setting BLOCWERK_DEV_USER to the identifier you want to act as.
        if (app.Environment.IsDevelopment()
            && !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("BLOCWERK_DEV_USER")))
        {
            app.UseMiddleware<DevAuthenticationMiddleware>();
        }

        app.UseAuthorization();
        app.UseAntiforgery();
        return app;
    }

    /// <summary>
    /// A signed-in human: cookie for the Blazor app, JWT for the token callers. Naming the schemes
    /// is what excludes the API key, because the evaluator only merges the identities it names.
    /// The assertion is belt and braces, for the case where the policy is evaluated against a
    /// principal handed in directly — Blazor's AuthorizeRouteView does exactly that, and no
    /// scheme re-authentication happens there.
    /// </summary>
    public static AuthorizationPolicy BuildHumanPolicy(AuthorizationPolicyBuilder policy)
    {
        return policy
            .AddAuthenticationSchemes(
                CookieAuthenticationDefaults.AuthenticationScheme,
                JwtBearerDefaults.AuthenticationScheme)
            .RequireAuthenticatedUser()
            .RequireAssertion(context => !context.User.IsApiKeyPrincipal())
            .Build();
    }

    /// <summary>
    /// The gallery byte route is reachable two ways: a signed-in human, or an anonymous viewer
    /// holding a wall's share token (validated by the endpoint itself). A machine caller has its
    /// own guarded route under /api and is rejected here, so a leaked wall key cannot walk the
    /// galleries of every other wall its owner belongs to.
    /// </summary>
    public static AuthorizationPolicy BuildGalleryImagePolicy(AuthorizationPolicyBuilder policy)
    {
        return policy
            .AddAuthenticationSchemes(
                CookieAuthenticationDefaults.AuthenticationScheme,
                JwtBearerDefaults.AuthenticationScheme)
            .AddRequirements(new WallGalleryImageRequirement())
            .Build();
    }

    private static void BuildApiKeyPolicy(AuthorizationPolicyBuilder policy, ApiKeyScope? scope)
    {
        policy.AddAuthenticationSchemes(ApiKeyAuthenticationHandler.SchemeName);
        policy.RequireAuthenticatedUser();
        string[] allowedScopes = scope is null
            ? [ApiKeyScope.Wall.ToString(), ApiKeyScope.User.ToString()]
            : [scope.Value.ToString()];
        policy.RequireClaim(ApiKeyClaimTypes.Scope, allowedScopes);
    }
}
