using Blocwerk.Authentication;
using Blocwerk.Authentication.Controllers;
using Blocwerk.Core;
using Blocwerk.Core.Services;
using Blocwerk.HoldDetection;
using Blocwerk.Web.Components;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc;
using Serilog;

namespace Blocwerk.Web;

public static class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Console(restrictedToMinimumLevel: Serilog.Events.LogEventLevel.Information)
            .WriteTo.File("logs/blocwerk.log", rollingInterval: RollingInterval.Day)
            .CreateLogger();

        builder.Host.UseSerilog();

        builder.Services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
            options.KnownIPNetworks.Clear();
            options.KnownProxies.Clear();
        });

        builder.ConfigureCoreServices(out var settings)
            .ConfigureAuthenticationAndAuthorization(settings)
            .ConfigureHoldDetection(settings);

        builder.Services.AddControllersWithViews()
            .AddApplicationPart(typeof(AccountController).Assembly);

        builder.Services.AddRazorComponents()
            .AddInteractiveServerComponents();

        builder.Services.AddRazorPages();
        builder.Services.AddServerSideBlazor();

        var app = builder.Build();

        app.UseForwardedHeaders();

        app.Use(async (context, next) =>
        {
            var headers = context.Response.Headers;
            headers["Strict-Transport-Security"] = "max-age=31536000; includeSubDomains";
            headers["X-Content-Type-Options"] = "nosniff";
            headers["X-Frame-Options"] = "DENY";
            headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
            headers["Content-Security-Policy"] =
                "default-src 'self'; " +
                "script-src 'self' 'unsafe-inline' 'unsafe-eval'; " +
                "style-src 'self' 'unsafe-inline'; " +
                "img-src 'self' data: blob:; " +
                "connect-src 'self' wss:; " +
                "font-src 'self'; " +
                "frame-ancestors 'none'";
            await next();
        });

        app.UseStaticFiles();
        app.ConfigureCoreApplication();
        app.ConfigureAuthenticationMiddlewares();
        app.MapControllers();

        app.MapRazorComponents<BlocwerkApp>()
            .AddInteractiveServerRenderMode();

        app.MapGet("/api/walls/{wallId:guid}/photo", async (
            Guid wallId,
            [FromQuery] string? token,
            [FromServices] IWallService wallService) =>
        {
            byte[]? photo;
            if (!string.IsNullOrEmpty(token))
            {
                photo = await wallService.GetPhotoByShareTokenAsync(wallId, token);
            }
            else
            {
                photo = await wallService.GetPhotoAsync(wallId);
            }

            return photo == null ? Results.NotFound() : Results.File(photo, "image/jpeg");
        });

        app.Run();
    }
}
