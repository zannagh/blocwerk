using System.Text;
using Blocwerk.Authentication.Authorization;
using Blocwerk.Authentication.Handlers;
using Blocwerk.Authentication.Middleware;
using Blocwerk.Authentication.Providers;
using Blocwerk.Authentication.Services;
using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Configuration;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services.TopLogger;
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

        // TOTP second factor: stateless, so a singleton. Uses the persisted DataProtection key ring to
        // encrypt the shared secret at rest (see the "blocwerk.totp" protector inside TotpService).
        app.Services.AddSingleton<ITotpService, TotpService>();
        app.Services.AddSingleton<IAuthorizationHandler, WallGalleryImageHandler>();

        // App-wide admin gate. Scoped, not singleton, because it resolves the current user through the
        // scoped ICurrentUserService — the Admin role is not a claim, so it must be read per request.
        app.Services.AddScoped<IAuthorizationHandler, AppAdminHandler>();

        // TopLogger token pair, encrypted at rest with the persisted DataProtection key ring
        // (protector "blocwerk.toplogger"). Lives here — with the DataProtection stack — while
        // Blocwerk.Core owns only the ITopLoggerTokenStore interface, so there is no circular reference.
        app.Services.AddScoped<ITopLoggerTokenStore, DataProtectionTopLoggerTokenStore>();

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
            // A bare [Authorize] must never be satisfiable by an API-key principal. That is done
            // by the assertion inside BuildHumanPolicy, NOT by naming schemes on the policy: a
            // policy that names schemes also challenges every one of them, so a signed-out human
            // got the JWT handler's bare 401 instead of the cookie handler's redirect to the
            // login page. See BuildHumanPolicy for the full reasoning.
            options.DefaultPolicy = BuildHumanPolicy(new AuthorizationPolicyBuilder());

            // No FallbackPolicy on purpose: it would apply to every endpoint without authorization
            // metadata, which includes the login routes and the anonymous share-token routes.
            options.AddPolicy(BlocwerkPolicies.WallApiKey, policy => BuildApiKeyPolicy(policy, ApiKeyScope.Wall));
            options.AddPolicy(BlocwerkPolicies.UserApiKey, policy => BuildApiKeyPolicy(policy, ApiKeyScope.User));
            options.AddPolicy(BlocwerkPolicies.AnyApiKey, policy => BuildApiKeyPolicy(policy, null));
            options.AddPolicy(BlocwerkPolicies.WallGalleryImage, BuildGalleryImagePolicy(new AuthorizationPolicyBuilder()));
            options.AddPolicy(BlocwerkPolicies.AppAdmin, policy =>
            {
                policy.RequireAuthenticatedUser();
                policy.AddRequirements(new AppAdminRequirement());
            });
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
    /// A signed-in human: cookie for the Blazor app, JWT for the token callers. The API key is
    /// excluded by the assertion, not by a scheme list.
    /// </summary>
    /// <remarks>
    /// Deliberately names NO authentication schemes. A policy that names schemes challenges all of
    /// them on failure, so a signed-out visitor to an [Authorize] Blazor page got the JWT handler's
    /// bare 401 rather than the cookie handler's 302 to /account/login. With no schemes named,
    /// both authentication and challenge run through the app's default "BlocwerkPolicy" forwarder,
    /// which redirects — and nothing is lost on the security side: <see cref="ApiKeySurface"/>
    /// only forwards a <c>bwk_</c> bearer to the API-key scheme under /api/walls and /api/v1, so
    /// on any other path an API-key principal never exists in the first place, and where one does
    /// exist the assertion below rejects it. The assertion also covers the case where the policy
    /// is evaluated against a principal handed in directly — Blazor's AuthorizeRouteView does
    /// exactly that, and no scheme re-authentication happens there.
    /// </remarks>
    public static AuthorizationPolicy BuildHumanPolicy(AuthorizationPolicyBuilder policy)
    {
        return policy
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
    /// <remarks>
    /// Names no schemes, for the same reason as <see cref="BuildHumanPolicy"/>: an anonymous
    /// viewer arriving without a share token must be redirected to the login page, and a policy
    /// that names schemes would challenge the JWT handler too and answer with a bare 401. The
    /// API-key rejection lives in <see cref="WallGalleryImageHandler"/>, not in a scheme list.
    /// </remarks>
    public static AuthorizationPolicy BuildGalleryImagePolicy(AuthorizationPolicyBuilder policy)
    {
        return policy
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
