using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Reflection;
using Blocwerk.Authentication;
using Blocwerk.Authentication.Controllers;
using Blocwerk.Core;
using Blocwerk.Core.Services;
using Blocwerk.Core.Telemetry;
using Blocwerk.HoldDetection;
using Blocwerk.Web.Components;
using Blocwerk.Web.Controllers;
using Blocwerk.Web.State;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;
using Serilog.Sinks.OpenTelemetry;

namespace Blocwerk.Web;

public static class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        var serviceName = "Blocwerk";
        var serviceVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.1.0";
        var otlpEndpoint = builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"];

        var loggerConfiguration = new LoggerConfiguration()

            // Information by default. Debug produced ~200k rows/day here — almost entirely EF Core
            // connection/reader chatter and Kestrel keep-alive noise — which drowned the handful of
            // real warnings/errors in the log and in the OTLP export. Framework categories are
            // pinned to Warning so only genuine problems (and our own Information logs) ship.
            .MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft.EntityFrameworkCore", Serilog.Events.LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.AspNetCore", Serilog.Events.LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.AspNetCore.Hosting.Diagnostics", Serilog.Events.LogEventLevel.Information)
            .MinimumLevel.Override("Microsoft.Hosting.Lifetime", Serilog.Events.LogEventLevel.Information)
            .Enrich.FromLogContext()
            .WriteTo.Console(restrictedToMinimumLevel: Serilog.Events.LogEventLevel.Information)
            .WriteTo.File("logs/blocwerk.log", rollingInterval: RollingInterval.Day);

        // Ship logs to the OTLP collector so they land next to traces and metrics (e.g. the
        // Aspire dashboard's Structured Logs view). Serilog is the one log pipeline here —
        // UseSerilog() routes every ILogger<T> through it — so exporting from the sink captures
        // both the framework/ILogger logs and the static Log.* calls in a single place. Without
        // this the OTLP endpoint saw no logs at all, because UseSerilog(writeToProviders: false)
        // detaches the Microsoft.Extensions.Logging providers.
        if (!string.IsNullOrEmpty(otlpEndpoint))
        {
            loggerConfiguration.WriteTo.OpenTelemetry(options =>
            {
                options.Endpoint = otlpEndpoint;
                options.Protocol = OtlpProtocol.Grpc;
                options.ResourceAttributes.Add("service.name", serviceName);
                options.ResourceAttributes.Add("service.version", serviceVersion);
            });
        }

        Log.Logger = loggerConfiguration.CreateLogger();

        builder.Host.UseSerilog();

        // Register the custom instruments (incl. observable gauges) before the meter is exported.
        BlocwerkMetrics.Initialize();

        builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(serviceName: serviceName, serviceVersion: serviceVersion))
            .WithTracing(tracing =>
            {
                tracing
                    .AddSource(Otel.ActivitySource.Name)
                    .AddAspNetCoreInstrumentation(options =>
                    {
                        options.RecordException = true;
                        options.EnrichWithHttpRequest = (activity, httpRequest) =>
                        {
                            activity.SetTag("http.request.path", httpRequest.Path);
                        };
                        options.EnrichWithHttpResponse = (activity, httpResponse) =>
                        {
                            activity.SetTag("http.response.status_code", httpResponse.StatusCode);
                        };
                    })
                    .AddHttpClientInstrumentation(options => { options.RecordException = true; });

                if (!string.IsNullOrEmpty(otlpEndpoint))
                {
                    tracing.AddOtlpExporter();
                }
            })
            .WithMetrics(metrics =>
            {
                metrics
                    .AddMeter(Otel.Meter.Name)
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddRuntimeInstrumentation()

                    // Keeps the latest values in memory and serves them at /metrics for scraping,
                    // independent of whether an OTLP collector is configured (see MapPrometheusScrapingEndpoint).
                    .AddPrometheusExporter();

                if (!string.IsNullOrEmpty(otlpEndpoint))
                {
                    metrics.AddOtlpExporter();
                }
            });

        // Logs are exported to OTLP by the Serilog OpenTelemetry sink configured above, so no
        // separate Microsoft.Extensions.Logging OTLP provider is registered here.
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
            .AddInteractiveServerComponents(options =>
            {
                // Phones on gym wifi drop the websocket constantly. If the server discards the
                // circuit before the client reconnects, the reconnect is rejected and the user is
                // forced to reload (losing in-page state). The client retries for minutes
                // (see blazor-boot.js), so hold the disconnected circuit's state longer to match —
                // a walk-out-of-signal-and-back-in recovers in place instead of hitting "Reload".
                options.DisconnectedCircuitRetentionPeriod = TimeSpan.FromMinutes(5);
                options.DisconnectedCircuitMaxRetained = 200;
            })
            .AddHubOptions(o =>
            {
                // Blazor Server pushes JS interop return values through the SignalR
                // hub; the 32 KB default trips as soon as JS returns a stitched PNG
                // or any other >32 KB payload. 64 MB is well above anything the
                // stitcher or a wall photo would produce.
                o.MaximumReceiveMessageSize = 64 * 1024 * 1024;
            });

        builder.Services.AddRazorPages();
        builder.Services.AddServerSideBlazor();

        // Per-circuit cache of the user's live session, so the tab bar and the activity page share
        // one source of truth and the "Session" indicator lights up the moment a session starts.
        builder.Services.AddScoped<SessionState>();

        // Counts live circuits into the "connected users" gauge.
        builder.Services.AddScoped<CircuitHandler, TelemetryCircuitHandler>();

        ConfigureApiCookieBehaviour(builder);

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
                "style-src 'self' 'unsafe-inline' https://fonts.googleapis.com; " +
                "img-src 'self' data: blob:; " +
                "connect-src 'self' ws: wss:; " +
                "font-src 'self' https://fonts.gstatic.com; " +
                "frame-ancestors 'none'";

            // The service worker is served from /js/ but must control the whole origin. A worker
            // may only claim a scope broader than its own path when the server opts in with this
            // header (see wwwroot/js/pwa.js).
            if (context.Request.Path.Equals("/js/service-worker.js", StringComparison.OrdinalIgnoreCase))
            {
                headers["Service-Worker-Allowed"] = "/";
            }

            await next();
        });

        app.MapStaticAssets();
        app.ConfigureCoreApplication();
        app.ConfigureAuthenticationMiddlewares();
        app.MapControllers();

        // Prometheus/OpenMetrics scrape endpoint for the custom + runtime + ASP.NET metrics.
        // Handy for a quick `curl http://<host>:5050/metrics` when the dashboard isn't in reach.
        // It is unauthenticated and carries operational counts (no PII; wall ids are hashed), so
        // keep it off any public reverse-proxy route or scrape it only from the internal network.
        app.MapPrometheusScrapingEndpoint();

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

        // The wall photo as it looked at a given generation, so historic boulders can
        // be rendered against the wall they were actually set on.
        app.MapGet("/api/walls/{wallId:guid}/photo/{generation:int}", async (
            Guid wallId,
            int generation,
            [FromQuery] string? token,
            [FromServices] IWallService wallService) =>
        {
            var photo = string.IsNullOrEmpty(token)
                ? await wallService.GetPhotoForGenerationAsync(wallId, generation)
                : await wallService.GetPhotoForGenerationByShareTokenAsync(wallId, token, generation);

            return photo == null
                ? Results.NotFound()
                : Results.File(photo.Photo, photo.ContentType ?? "image/jpeg");
        });

        app.MapGet("/api/walls/{wallId:guid}/staged-photo", async (
            Guid wallId,
            [FromServices] IWallService wallService) =>
        {
            var photo = await wallService.GetStagedPhotoAsync(wallId);
            return photo == null ? Results.NotFound() : Results.File(photo, "image/jpeg");
        });

        app.Run();
    }

    /// <summary>
    /// Two adjustments the offline queue depends on, applied after
    /// <c>ConfigureAuthenticationAndAuthorization</c> has registered the cookie handler.
    /// <list type="number">
    /// <item>Pin the auth cookie to <c>SameSite=Lax</c>. This is already the framework default,
    /// but the offline endpoints lean on it as half of their CSRF defence (see
    /// <see cref="RequireClientHeaderAttribute"/>), so it is stated rather than inherited.</item>
    /// <item>Answer <c>401</c> instead of redirecting to the login page for <c>/api</c> requests.
    /// Without this a queued replay made after the session expired would follow the 302, receive
    /// the login page with status 200, and the queue would wrongly treat the action as applied
    /// and drop it.</item>
    /// </list>
    /// </summary>
    private static void ConfigureApiCookieBehaviour(WebApplicationBuilder builder)
    {
        builder.Services.PostConfigure<CookieAuthenticationOptions>(
            CookieAuthenticationDefaults.AuthenticationScheme,
            options =>
            {
                options.Cookie.SameSite = SameSiteMode.Lax;

                var redirectToLogin = options.Events.OnRedirectToLogin;
                options.Events.OnRedirectToLogin = context =>
                {
                    if (IsApiRequest(context.Request))
                    {
                        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                        return Task.CompletedTask;
                    }

                    return redirectToLogin(context);
                };

                var redirectToAccessDenied = options.Events.OnRedirectToAccessDenied;
                options.Events.OnRedirectToAccessDenied = context =>
                {
                    if (IsApiRequest(context.Request))
                    {
                        context.Response.StatusCode = StatusCodes.Status403Forbidden;
                        return Task.CompletedTask;
                    }

                    return redirectToAccessDenied(context);
                };
            });
    }

    private static bool IsApiRequest(HttpRequest request)
    {
        return request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase);
    }
}
