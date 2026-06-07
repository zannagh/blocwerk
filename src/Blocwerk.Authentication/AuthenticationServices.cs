using System.Text;
using Blocwerk.Authentication.Handlers;
using Blocwerk.Authentication.Providers;
using Blocwerk.Authentication.Services;
using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Configuration;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components.Authorization;
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
        app.Services.AddDataProtection();

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
        return app;
    }

    public static WebApplication ConfigureAuthenticationMiddlewares(this WebApplication app)
    {
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseAntiforgery();
        return app;
    }
}
