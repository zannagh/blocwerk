using System.Security.Claims;
using System.Text;
using Blocwerk.Authentication.Handlers;
using Blocwerk.Authentication.Providers;
using Blocwerk.Authentication.Services;
using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Configuration;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Tokens;
using Serilog;

namespace Blocwerk.Authentication;

public static class AuthenticationServices
{
    public static IHostApplicationBuilder ConfigureAuthenticationAndAuthorization(this IHostApplicationBuilder app, BlocwerkSettings configuration)
    {
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

    public static IHostApplicationBuilder ConfigureDevAuthentication(this IHostApplicationBuilder app)
    {
        app.Services.AddHttpContextAccessor();
        app.Services.AddAuthenticationCore();
        app.Services.AddCascadingAuthenticationState();
        app.Services.AddDataProtection();

        app.Services.AddScoped<ICurrentUserService, DevCurrentUserService>();
        app.Services.AddScoped<AuthenticationStateProvider, DevAuthenticationStateProvider>();

        app.Services.AddAuthentication("Dev")
            .AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, DevAuthHandler>("Dev", _ => { });

        app.Services.AddAuthorization(options =>
        {
            options.DefaultPolicy = new AuthorizationPolicyBuilder("Dev")
                .RequireAssertion(_ => true)
                .Build();
        });

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

public class DevAuthHandler : Microsoft.AspNetCore.Authentication.AuthenticationHandler<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions>
{
    public DevAuthHandler(
        Microsoft.Extensions.Options.IOptionsMonitor<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions> options,
        Microsoft.Extensions.Logging.ILoggerFactory logger,
        System.Text.Encodings.Web.UrlEncoder encoder)
        : base(options, logger, encoder)
    {
    }

    protected override Task<Microsoft.AspNetCore.Authentication.AuthenticateResult> HandleAuthenticateAsync()
    {
        var claims = new[]
        {
            new Claim(ClaimTypes.Name, "dev"),
            new Claim(ClaimTypes.NameIdentifier, "local"),
        };
        var identity = new ClaimsIdentity(claims, "Dev");
        var principal = new ClaimsPrincipal(identity);
        var ticket = new Microsoft.AspNetCore.Authentication.AuthenticationTicket(principal, "Dev");
        return Task.FromResult(Microsoft.AspNetCore.Authentication.AuthenticateResult.Success(ticket));
    }
}

public class DevAuthenticationStateProvider : AuthenticationStateProvider
{
    public override Task<AuthenticationState> GetAuthenticationStateAsync()
    {
        var claims = new[]
        {
            new Claim(ClaimTypes.Name, "dev"),
            new Claim(ClaimTypes.NameIdentifier, "local"),
        };
        var identity = new ClaimsIdentity(claims, "Dev");
        var principal = new ClaimsPrincipal(identity);
        return Task.FromResult(new AuthenticationState(principal));
    }
}
