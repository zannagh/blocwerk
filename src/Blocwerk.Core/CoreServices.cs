using Blocwerk.Core.Configuration;
using Blocwerk.Core.Data;
using Blocwerk.Core.Services;
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

        if (IsDevWallSnapshotEnabled(builder))
        {
            // Singleton: shared in-memory wall state across all circuits / requests in dev.
            builder.Services.AddSingleton<IWallService, DevSnapshotWallService>();
        }
        else
        {
            builder.Services.AddScoped<IWallService, WallService>();
        }

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
            // In dev (snapshot mode) PG may be unreachable after the first seed —
            // keep the app starting so hot reload still works.
            logger.LogWarning(ex, "Skipping EF migrations in Development (PG unreachable?). Snapshot mode will continue if a local snapshot exists.");
        }

        return app;
    }

    private static bool IsDevWallSnapshotEnabled(IHostApplicationBuilder builder)
    {
        if (!builder.Environment.IsDevelopment())
        {
            return false;
        }

        var flag = Environment.GetEnvironmentVariable("BLOCWERK_DEV_WALL_SNAPSHOT")
                   ?? builder.Configuration["BLOCWERK_DEV_WALL_SNAPSHOT"];
        return string.Equals(flag, "true", StringComparison.OrdinalIgnoreCase)
               || string.Equals(flag, "1", StringComparison.Ordinal);
    }
}
