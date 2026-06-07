using Blocwerk.Core.Configuration;
using Blocwerk.Core.Data;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

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
        builder.Services.AddScoped<IWallService, WallService>();
        builder.Services.AddScoped<IBoulderService, BoulderService>();
        builder.Services.AddScoped<IAttemptService, AttemptService>();
        builder.Services.AddScoped<ICommentService, CommentService>();

        return builder;
    }

    public static IHost ConfigureCoreApplication(this IHost app)
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BlocwerkDbContext>();
        db.Database.Migrate();
        return app;
    }
}
