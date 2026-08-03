using Blocwerk.Core.Configuration;
using Blocwerk.Core.Data;
using Blocwerk.Core.Services;
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
        var config = settings;
        builder.Services.AddSingleton(config);

        builder.Services.AddDbContextFactory<BlocwerkDbContext>(options =>
        {
            options.UseNpgsql(config.Postgres.ConnectionString);
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
        builder.Services.AddScoped<IWallSegmentService, WallSegmentService>();
        builder.Services.AddScoped<IProgressionService, ProgressionService>();
        builder.Services.AddScoped<ITrainingService, TrainingService>();
        builder.Services.AddScoped<ISessionService, SessionService>();

        // Polls the DB for the "how many exist now" telemetry gauges (walls, boulders, users...).
        builder.Services.AddHostedService<TelemetryStatsCollector>();

        builder.Services.AddScoped<IWallService, WallService>();

        return builder;
    }

    public static IHost ConfigureCoreApplication(this IHost app)
    {
        using var scope = app.Services.CreateScope();
        var env = scope.ServiceProvider.GetRequiredService<IHostEnvironment>();
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<BlocwerkDbContext>>();

        try
        {
            var db = scope.ServiceProvider.GetRequiredService<BlocwerkDbContext>();
            db.Database.Migrate();
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
        }

        return app;
    }
}
