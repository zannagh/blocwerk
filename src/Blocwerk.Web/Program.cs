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
using Blocwerk.Web.Endpoints;
using Blocwerk.Web.HealthChecks;
using Blocwerk.Web.Maintenance;
using Blocwerk.Web.State;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Diagnostics.HealthChecks;
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

                // Tolerate briefly-quiet clients (backgrounded mobile tab, a network blip) before
                // tearing the circuit down: a dropped circuit is what flashes the "something went
                // wrong" UI. Server pings every 15s; a client has 60s (up from the 30s default) of
                // silence before it's considered gone.
                o.KeepAliveInterval = TimeSpan.FromSeconds(15);
                o.ClientTimeoutInterval = TimeSpan.FromSeconds(60);
            });

        builder.Services.AddRazorPages();
        builder.Services.AddServerSideBlazor();

        // The boulder create/revise pages read the experience cookie server-side on the initial
        // (prerender) load so the right experience renders with no client-side flash.
        builder.Services.AddHttpContextAccessor();

        // Per-circuit cache of the user's live session, so the tab bar and the activity page share
        // one source of truth and the "Session" indicator lights up the moment a session starts.
        builder.Services.AddScoped<SessionState>();

        // Per-circuit cache of wall/boulder aggregate reads, invalidated across circuits by the
        // domain-change notifier so revisits are served from memory and edits live-refresh.
        builder.Services.AddScoped<WallCacheState>();

        // Counts live circuits into the "connected users" gauge.
        builder.Services.AddScoped<CircuitHandler, TelemetryCircuitHandler>();

        // Primes IKioskContext at circuit start, while the connection's HttpContext is still on the
        // stack, so the kiosk device cookie is captured once and held for the circuit's life.
        builder.Services.AddScoped<CircuitHandler, KioskCircuitHandler>();

        // In-memory brute-force throttle for kiosk registration and kiosk PIN attempts. Singleton so
        // it spans circuits and requests; deliberately not the user-row login lockout (see the type).
        builder.Services.AddSingleton<KioskThrottleRegistry>();

        // Device pairings in flight: the tablet's circuit, the approving admin's circuit and the
        // completion HTTP request are three different scopes that must see the same entry, so this
        // is a singleton for the same reason the throttle is. Entries live three minutes and losing
        // them on restart costs a re-tap of "get a new code".
        builder.Services.AddSingleton<KioskPairingRegistry>();

        // The one routine both approval entry points go through. Scoped, because it resolves the
        // acting user from the ambient session rather than being handed a user id.
        builder.Services.AddScoped<KioskPairingApprover>();

        // Logs the ascent tapped on a kiosk boulder page before anybody was picked. Scoped: it
        // mutates the CURRENT request's principal so the attempt resolves as the member who just
        // signed in.
        builder.Services.AddScoped<KioskPendingAttemptLogger>();

        // App-wide, in-memory "busy" signal: the singleton registry tracks unsaved in-flight edits
        // across every circuit; the scoped wrapper is injected into the editing components and
        // releases its leases on circuit teardown (backstop against an abrupt disconnect).
        builder.Services.AddSingleton<EditActivityRegistry>();
        builder.Services.AddScoped<CircuitEditActivity>();

        // Admin-triggered maintenance. The runner is a singleton because a job outlives the circuit
        // that started it; it takes an EditActivityRegistry lease for its duration, so a run holds
        // /health/ready-to-deploy at 503 and the autodeploy hook waits instead of recreating the
        // container underneath it. Nothing here runs on its own — there is no hosted service.
        builder.Services.AddSingleton<MaintenanceJobRunner>();
        builder.Services.AddScoped<ImageVariantWarmer>();
        builder.Services.AddScoped<AvatarNormalizer>();

        // Health checks: "busy" (Degraded while editing, not Unhealthy) gates deploys; "database"
        // probes PostgreSQL. Both are surfaced anonymously via MapHealthChecks below.
        builder.Services.AddHealthChecks()
            .AddCheck<BusyHealthCheck>("busy", tags: new[] { "busy" })
            .AddCheck<DatabaseHealthCheck>("database", tags: new[] { "db" });

        ConfigureApiCookieBehaviour(builder);

        var app = builder.Build();

        app.UseForwardedHeaders();

        // A cookie that no longer names anybody who may sign in must land on a sign-in prompt, not a
        // 500 the user cannot read their way out of. Narrow by design — see the class docs.
        app.UseUnresolvableSessionRedirect();

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

        // Full health report as JSON (overall status + each check's name/status/description/data,
        // so the "busy" entry is visible). Anonymous by default — there is no FallbackPolicy and
        // /health is not under /api, so the cookie handler never redirects it. DB-down => overall
        // Unhealthy => 503; busy (Degraded) keeps the overall report at 200.
        app.MapHealthChecks("/health", new HealthCheckOptions
        {
            ResponseWriter = HealthCheckResponseWriter.WriteAsync,
        });

        // Deploy gate: only the "busy" check, mapped so idle => 200 and busy => 503. The deploy
        // hook polls this and waits while it returns 503. The plain-text body is a stable token —
        // "idle" only when the busy check is Healthy, "busy" otherwise — so the hook asserts on the
        // body, not just the 2xx, and a fail-open (a 200 that somehow isn't idle) can't slip past.
        app.MapHealthChecks("/health/ready-to-deploy", new HealthCheckOptions
        {
            Predicate = r => r.Tags.Contains("busy"),
            ResultStatusCodes =
            {
                [HealthStatus.Healthy] = StatusCodes.Status200OK,
                [HealthStatus.Degraded] = StatusCodes.Status503ServiceUnavailable,
                [HealthStatus.Unhealthy] = StatusCodes.Status503ServiceUnavailable,
            },
            ResponseWriter = (context, report) =>
            {
                context.Response.ContentType = "text/plain";
                return context.Response.WriteAsync(report.Status == HealthStatus.Healthy ? "idle" : "busy");
            },
        });

        app.MapRazorComponents<BlocwerkApp>()
            .AddInteractiveServerRenderMode();

        // The wall photo routes (current, per generation, staged). They sit under /api/walls, so
        // they are covered by WallPhotoEndpointAuthorizationTests.
        app.MapWallPhotos();

        // Per-panel photos for big (multi-image) walls; same browser-only auth posture as the
        // wall photo routes above.
        app.MapWallPanelPhotos();

        // User avatar bytes; same browser-only auth posture as the wall photo routes above.
        app.MapUserAvatars();

        // Beta clips and their poster frames. Access mirrors the wall photo routes above: a share
        // token takes the anonymous path, otherwise the caller must be signed in.
        // enableRangeProcessing matters here — without it <video> cannot seek, and Safari refuses
        // to play at all because it opens every media element with a Range request.
        app.MapGet("/api/beta-videos/{videoId:guid}", async (
            Guid videoId,
            [FromQuery] string? token,
            [FromServices] IBetaVideoService betaVideoService) =>
        {
            try
            {
                var video = await betaVideoService.GetVideoFileAsync(videoId, token);
                if (video is null)
                {
                    return Results.NotFound();
                }

                // Stream disk-backed clips straight from the file (never load them whole into memory);
                // fall back to the legacy in-database bytes. Range processing lets <video> seek.
                return video.PhysicalPath is not null
                    ? Results.File(video.PhysicalPath, video.ContentType, enableRangeProcessing: true)
                    : Results.File(video.Bytes!, video.ContentType, enableRangeProcessing: true);
            }
            catch (UnauthorizedAccessException)
            {
                return Results.Unauthorized();
            }
        });

        app.MapBetaVideoUpload();

        // Gallery item bytes for the signed-in browser (uploads, the wall photo, retired
        // generation photos). Lives outside /api because /api is the API-key surface.
        app.MapWallGalleryImages();

        app.MapGet("/api/beta-videos/{videoId:guid}/thumbnail", async (
            Guid videoId,
            [FromQuery] string? token,
            [FromServices] IBetaVideoService betaVideoService) =>
        {
            try
            {
                var thumbnail = await betaVideoService.GetThumbnailAsync(videoId, token);
                return thumbnail == null
                    ? Results.NotFound()
                    : Results.File(thumbnail.Data, thumbnail.ContentType);
            }
            catch (UnauthorizedAccessException)
            {
                return Results.Unauthorized();
            }
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
