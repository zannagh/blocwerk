using System.Text;
using Blocwerk.Authentication.Handlers;
using Blocwerk.Authentication.Middleware;
using Blocwerk.Authentication.Providers;
using Blocwerk.Authentication.Services;
using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Configuration;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
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

        var policyScheme = "BlocwerkPolicy";

        app.Services.AddAuthentication(policyScheme)
            .AddPolicyScheme(policyScheme, policyScheme, options =>
            {
                options.ForwardDefaultSelector = context =>
                {
                    var authHeader = context.Request.Headers.Authorization.FirstOrDefault();
                    if (!string.IsNullOrEmpty(authHeader) && authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                    {
                        return JwtBearerDefaults.AuthenticationScheme;
                    }

                    return CookieAuthenticationDefaults.AuthenticationScheme;
                };
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
            });

        app.Services.AddAuthorization();

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
}
