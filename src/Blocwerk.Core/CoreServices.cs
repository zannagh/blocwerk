using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Configuration;
using Blocwerk.Core.Data;
using Blocwerk.Core.Services;
using Blocwerk.Core.Services.TopLogger;
using Blocwerk.Core.Telemetry;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core;

public static class CoreServices
{
    public static IHostApplicationBuilder ConfigureCoreServices(
        this IHostApplicationBuilder builder,
        out BlocwerkSettings settings)
    {
        builder.Configuration
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
            .AddEnvironmentVariables();

        settings = new BlocwerkSettings(builder.Configuration);

        // Dev auto-login (BLOCWERK_DEV_USER) grants the dev user admin via DevWallAdminSeeder. Feed that
        // identifier into the effective AdminIdentifiers so the config-authoritative admin reconciliation
        // in CurrentUserService keeps the dev user an admin instead of revoking it on the next resolve.
        // Development only — in production BLOCWERK_DEV_USER is never honoured, so it must not grant admin.
        if (builder.Environment.IsDevelopment())
        {
            var devUser = Environment.GetEnvironmentVariable("BLOCWERK_DEV_USER");
            if (!string.IsNullOrWhiteSpace(devUser) && !settings.AdminIdentifiers.Contains(devUser))
            {
                settings.AdminIdentifiers.Add(devUser);
            }
        }

        var config = settings;
        builder.Services.AddSingleton(config);

        // In-process pub/sub for wall/boulder changes + the EF interceptor that publishes them.
        // Singletons so a mutation on any circuit invalidates every circuit's cache (see
        // IDomainChangeNotifier / DomainChangeInterceptor).
        builder.Services.AddSingleton<IDomainChangeNotifier, DomainChangeNotifier>();
        builder.Services.AddSingleton<DomainChangeInterceptor>();

        builder.Services.AddDbContextFactory<BlocwerkDbContext>((sp, options) =>
        {
            options.UseNpgsql(config.Postgres.ConnectionString);
            options.AddInterceptors(sp.GetRequiredService<DomainChangeInterceptor>());
        });

        builder.Services.AddScoped(sp =>
        {
            var factory = sp.GetRequiredService<IDbContextFactory<BlocwerkDbContext>>();
            return factory.CreateDbContext();
        });

        builder.Services.AddScoped<IActivityLogService, ActivityLogService>();
        builder.Services.AddScoped<IBoulderService, BoulderService>();
        builder.Services.AddScoped<IBoulderFeedbackService, BoulderFeedbackService>();
        builder.Services.AddScoped<IAttemptService, AttemptService>();
        builder.Services.AddScoped<ICommentService, CommentService>();
        builder.Services.AddSingleton<IBetaVideoStorage, FileSystemBetaVideoStorage>();
        builder.Services.AddSingleton<IVideoTranscoder, FfmpegVideoTranscoder>();
        builder.Services.AddScoped<IBetaVideoService, BetaVideoService>();
        builder.Services.AddSingleton<IWallImageStorage, FileSystemWallImageStorage>();
        builder.Services.AddScoped<IWallImageService, WallImageService>();
        builder.Services.AddScoped<IWallTemperatureService, WallTemperatureService>();
        builder.Services.AddScoped<IApiKeyService, ApiKeyService>();
        builder.Services.AddScoped<IWallSegmentService, WallSegmentService>();
        builder.Services.AddScoped<IProgressionService, ProgressionService>();
        builder.Services.AddScoped<ITrainingService, TrainingService>();
        builder.Services.AddScoped<ISessionService, SessionService>();
        builder.Services.AddScoped<IAccountMergeService, AccountMergeService>();
        builder.Services.AddScoped<IAdminDashboardService, AdminDashboardService>();

        // Password login: the hasher is stateless (singleton); the credential/lookup service is scoped
        // like the other DB services.
        builder.Services.AddSingleton<IPasswordService, PasswordService>();
        builder.Services.AddScoped<IPasswordLoginService, PasswordLoginService>();

        // Per-user, persisted brute-force lockout shared by the password and TOTP login endpoints.
        builder.Services.AddScoped<ILoginLockoutService, LoginLockoutService>();

        // Outgoing SMTP mail. Stateless (a MailKit client is created per send), so a singleton.
        // Not wired to any feature yet — callers gate on IEmailSender.IsConfigured.
        builder.Services.AddSingleton<IEmailSender, EmailSender>();

        // Reusable email verification codes (verify-email now; password-reset + signup later). Scoped
        // like the other DB services; codes are stored hashed and rate-limited per (email, purpose).
        builder.Services.AddScoped<IEmailVerificationService, EmailVerificationService>();

        // Polls the DB for the "how many exist now" telemetry gauges (walls, boulders, users...).
        builder.Services.AddHostedService<TelemetryStatsCollector>();

        builder.Services.AddScoped<IWallService, WallService>();
        builder.Services.AddScoped<IWallPanelService, WallPanelService>();
        builder.Services.AddScoped<IWallBigUpdateService, WallBigUpdateService>();

        ConfigureTopLogger(builder);

        return builder;
    }

    /// <summary>
    /// True when the exception indicates Postgres is not yet available (still starting/recovering or
    /// not accepting connections) — a transient startup condition worth retrying, as opposed to a
    /// real migration/schema error which must fail loudly.
    /// </summary>
    private static bool IsDatabaseUnavailable(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (e is Npgsql.PostgresException pg)
            {
                // 57xxx = operator intervention (incl. 57P03 "the database system is starting up");
                // 08xxx = connection exceptions. Any other SQL state (e.g. a real 42xxx migration
                // error) must NOT be retried.
                return pg.SqlState.StartsWith("57", StringComparison.Ordinal)
                    || pg.SqlState.StartsWith("08", StringComparison.Ordinal);
            }

            if (e is Npgsql.NpgsqlException)
            {
                // Socket/connection failure — Postgres isn't accepting connections yet.
                return true;
            }
        }

        return false;
    }

    public static IHost ConfigureCoreApplication(this IHost app)
    {
        using var scope = app.Services.CreateScope();
        var env = scope.ServiceProvider.GetRequiredService<IHostEnvironment>();
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<BlocwerkDbContext>>();

        try
        {
            var db = scope.ServiceProvider.GetRequiredService<BlocwerkDbContext>();

            // After a host reboot the Docker daemon restarts containers in arbitrary order (compose
            // depends_on does NOT apply on reboot), so Postgres may still be doing crash recovery
            // (57P03 "the database system is starting up") or not yet accepting connections. Retry
            // the migration instead of crashing the whole app on that transient condition.
            const int maxDbWaitAttempts = 30;
            var attempt = 0;
            while (true)
            {
                try
                {
                    db.Database.Migrate();
                    break;
                }
                catch (Exception ex) when (!env.IsDevelopment() && attempt < maxDbWaitAttempts && IsDatabaseUnavailable(ex))
                {
                    attempt++;
                    logger.LogWarning("Database not ready yet (attempt {Attempt}/{Max}): {Message}. Retrying in 5s…",
                        attempt, maxDbWaitAttempts, ex.Message);
                    System.Threading.Thread.Sleep(TimeSpan.FromSeconds(5));
                }
            }
        }
        catch (Exception ex) when (env.IsDevelopment())
        {
            // Keep the app starting under hot reload even if the dev Postgres is momentarily down.
            logger.LogWarning(ex, "Skipping EF migrations in Development (is the dev Postgres running?).");
        }

        if (env.IsDevelopment())
        {
            // One-time clone of a configured source (e.g. production) into the isolated dev DB.
            try
            {
                var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<BlocwerkDbContext>>();
                var settings = scope.ServiceProvider.GetRequiredService<BlocwerkSettings>();
                DevDataImporter.ImportIfNeededAsync(factory, settings, logger).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Dev data import failed; continuing with the current dev database contents.");
            }

            // Make the configured dev user (BLOCWERK_DEV_USER) an admin of every wall so local
            // testing can see and administer all walls without hand-seeding membership.
            try
            {
                var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<BlocwerkDbContext>>();
                DevWallAdminSeeder.SeedIfNeededAsync(factory, logger).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Dev wall-admin seed failed; continuing without all-wall admin.");
            }
        }

        // Reconstruct Activity rows for any events that predate the activity model. Idempotent, so it
        // is safe to run on every start; it no-ops once all events are grouped.
        try
        {
            var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<BlocwerkDbContext>>();
            ActivityBackfill.RunIfNeededAsync(factory, logger).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Activity backfill failed; existing events remain ungrouped until the next start.");
        }

        return app;
    }

    private static void ConfigureTopLogger(IHostApplicationBuilder builder)
    {
        // TopLogger import (Phase 1): the vendored GraphQL client plus the import/sync service. No
        // background HostedService yet — a sync is triggered from the profile UI. The token pair lives
        // encrypted per-user in the DataProtection-backed ITopLoggerTokenStore, registered alongside
        // the auth stack (Blocwerk.Authentication) so this project keeps no DataProtection dependency.
        var topLoggerSettings = builder.Configuration.GetSection("TopLogger").Get<TopLoggerSettings>()
            ?? new TopLoggerSettings();
        builder.Services.AddSingleton(topLoggerSettings);

        // Paces + stamps a browser User-Agent on the typed GraphQL client's requests.
        builder.Services.AddTransient<PacingHandler>();

        // Direct refresh call: the refresh token doubles as the Bearer header (see TopLoggerAuthService).
        builder.Services.AddHttpClient(TopLoggerAuthService.RefreshHttpClientName, client =>
        {
            if (!string.IsNullOrWhiteSpace(topLoggerSettings.UserAgent))
            {
                client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", topLoggerSettings.UserAgent);
            }
        });

        builder.Services.AddHttpClient<ITopLoggerGraphQlClient, TopLoggerGraphQlClient>()
            .AddHttpMessageHandler<PacingHandler>();

        builder.Services.AddScoped<ITopLoggerAuthService, TopLoggerAuthService>();
        builder.Services.AddScoped<ITopLoggerApiClient, TopLoggerApiClient>();
        builder.Services.AddScoped<ITopLoggerImportService, TopLoggerImportService>();
    }
}
