using Microsoft.Extensions.Configuration;

namespace Blocwerk.Core.Configuration;

/// <summary>
/// List-valued settings, read the way the scalar ones are: the <c>Blocwerk</c> section first (array
/// form, which also covers the long <c>BLOCWERK__A__B__0</c> env names), then a comma-separated short
/// env var as the fallback.
/// </summary>
internal static class ConfigurationLists
{
    public static List<string> Read(IConfigurationSection blocwerkSection, string key, string shortEnvName)
    {
        var entries = blocwerkSection.GetSection(key)
            .GetChildren()
            .Select(child => child.Value?.Trim())
            .Where(value => !string.IsNullOrEmpty(value))
            .Select(value => value!)
            .ToList();

        if (entries.Count > 0)
        {
            return entries;
        }

        // A scalar under the key (appsettings "a,b" or the long env name without an index).
        return Split(blocwerkSection[key] ?? Environment.GetEnvironmentVariable(shortEnvName));
    }

    private static List<string> Split(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
    }
}
