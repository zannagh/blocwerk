// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Enums;

namespace Blocwerk.Web.Controllers;

/// <summary>
/// The push notification types a member can opt out of on the profile page, by their wire names (the enum names, e.g.
/// <c>BoulderAdded</c>), and the opt-out mask turned into "receive this type: true or false".
/// </summary>
public static class ProfileNotifications
{
    /// <summary>Every single type, in the enum's order: the checkboxes the web profile page shows.</summary>
    public static IReadOnlyList<NotificationType> Types { get; } = Enum.GetValues<NotificationType>()
        .Where(t => t != NotificationType.None && (t & (t - 1)) == 0)
        .ToList();

    /// <summary>Reads a wire name, exactly as spelled.</summary>
    public static bool TryParse(string name, out NotificationType type)
    {
        type = Types.FirstOrDefault(t => t.ToString() == name);
        return type != NotificationType.None;
    }

    /// <summary>Each type mapped to true when the member receives it (its opt-out bit is clear).</summary>
    public static IReadOnlyDictionary<string, bool> Enabled(NotificationType disabled) =>
        Types.ToDictionary(t => t.ToString(), t => !disabled.HasFlag(t));
}
