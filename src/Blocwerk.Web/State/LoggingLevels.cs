// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Serilog;
using Serilog.Events;

namespace Blocwerk.Web.State;

/// <summary>
/// Log levels per category, from the standard <c>Logging:LogLevel</c> configuration section, i.e. environment variables
/// such as <c>Logging__LogLevel__Default=Information</c> or <c>Logging__LogLevel__Microsoft.AspNetCore.Components.Server.Circuits=Debug</c>
/// (Serilog is the one log pipeline, so it reads that section itself). The defaults below apply where the section says
/// nothing. A category's level also covers the categories below it; the longest name wins.
/// </summary>
public static class LoggingLevels
{
    public const string Circuits = "Microsoft.AspNetCore.Components.Server.Circuits";

    /// <summary>
    /// Debug for the circuit registry only: that is where the framework logs a circuit being evicted, a disconnect becoming
    /// permanent and reconnect attempts, a few lines per circuit. The rest of the Circuits namespace (renderer batches,
    /// per-event dispatch) would log per UI message at Debug, so it stays at Warning.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, LogEventLevel> Defaults = new Dictionary<string, LogEventLevel>
    {
        ["Default"] = LogEventLevel.Information,
        ["Microsoft.EntityFrameworkCore"] = LogEventLevel.Warning,
        ["Microsoft.AspNetCore"] = LogEventLevel.Warning,
        ["Microsoft.AspNetCore.Hosting.Diagnostics"] = LogEventLevel.Information,
        ["Microsoft.Hosting.Lifetime"] = LogEventLevel.Information,
        [Circuits] = LogEventLevel.Warning,
        [Circuits + ".CircuitRegistry"] = LogEventLevel.Debug,
    };

    public static LoggerConfiguration Apply(LoggerConfiguration logger, IConfiguration configuration)
    {
        var levels = Resolve(configuration);
        logger.MinimumLevel.Is(levels["Default"]);
        foreach (var (category, level) in levels.Where(l => l.Key != "Default"))
        {
            logger.MinimumLevel.Override(category, level);
        }

        return logger;
    }

    /// <summary>The defaults overlaid with the configured section.</summary>
    public static Dictionary<string, LogEventLevel> Resolve(IConfiguration configuration)
    {
        var levels = Defaults.ToDictionary(d => d.Key, d => d.Value);
        foreach (var entry in configuration.GetSection("Logging:LogLevel").GetChildren())
        {
            if (TryParse(entry.Value, out var level))
            {
                levels[entry.Key] = level;
            }
        }

        return levels;
    }

    private static bool TryParse(string? value, out LogEventLevel level)
    {
        level = LogEventLevel.Information;
        switch (value?.Trim().ToLowerInvariant())
        {
            case "trace": level = LogEventLevel.Verbose; return true;
            case "debug": level = LogEventLevel.Debug; return true;
            case "information": level = LogEventLevel.Information; return true;
            case "warning": level = LogEventLevel.Warning; return true;
            case "error": level = LogEventLevel.Error; return true;
            case "critical": level = LogEventLevel.Fatal; return true;
            default: return false;
        }
    }
}
