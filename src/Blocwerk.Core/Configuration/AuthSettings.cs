using Microsoft.Extensions.Configuration;

namespace Blocwerk.Core.Configuration;

/// <summary>
/// Opt-in authentication features that are OFF unless an operator switches them on
/// (<c>Blocwerk:Auth</c>, env <c>BLOCWERK__AUTH__*</c> or the short form <c>AUTH__*</c>).
/// </summary>
public class AuthSettings
{
    public ApiKeyLoginSettings ApiKeyLogin { get; set; } = new();

    /// <summary>
    /// Reads the section the same way <see cref="BlocwerkSettings"/> reads everything else: the
    /// <c>Blocwerk</c> configuration section first (which already covers the long
    /// <c>BLOCWERK__AUTH__…</c> env names), then the short env name as a fallback.
    /// </summary>
    public static AuthSettings Bind(IConfigurationSection blocwerkSection)
    {
        var enabled = blocwerkSection["Auth:ApiKeyLogin:Enabled"]
                      ?? Environment.GetEnvironmentVariable("AUTH__APIKEYLOGIN__ENABLED");

        return new AuthSettings
        {
            ApiKeyLogin = new ApiKeyLoginSettings
            {
                Enabled = bool.TryParse(enabled, out var parsed) && parsed,
                AllowedUserIds = BindAllowedUserIds(blocwerkSection),
            },
        };
    }

    /// <summary>
    /// The configured list (array form) when it has entries, else the comma-separated short env var.
    /// An entry that is not a user id fails startup: a typo must not silently lock someone out — or,
    /// worse, be mistaken for a working allow-list.
    /// </summary>
    private static HashSet<Guid> BindAllowedUserIds(IConfigurationSection blocwerkSection)
    {
        var entries = ConfigurationLists.Read(
            blocwerkSection, "Auth:ApiKeyLogin:AllowedUserIds", "AUTH__APIKEYLOGIN__ALLOWEDUSERIDS");

        var ids = new HashSet<Guid>();
        foreach (var entry in entries)
        {
            if (!Guid.TryParse(entry, out var id))
            {
                throw new InvalidOperationException(
                    $"Blocwerk:Auth:ApiKeyLogin:AllowedUserIds contains '{entry}', which is not a user id.");
            }

            ids.Add(id);
        }

        return ids;
    }
}
