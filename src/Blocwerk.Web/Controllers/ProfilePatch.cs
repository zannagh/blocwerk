// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Text.Json;
using Blocwerk.Core.Enums;

namespace Blocwerk.Web.Controllers;

/// <summary>
/// The body of <c>PATCH /api/v1/me/profile</c>, read as a merge patch: a field that is absent stays as it is, a field
/// that is present is set, and <c>null</c> clears the two fields that may be empty (display name, home wall).
/// </summary>
/// <param name="SetsDisplayName">True when <c>displayName</c> was sent.</param>
/// <param name="DisplayName">The custom display name; null or blank resets to the account name.</param>
/// <param name="SetsHomeWall">True when <c>homeWallId</c> was sent.</param>
/// <param name="HomeWallId">The home wall, or null for none.</param>
/// <param name="PreferFontGrades">True for Fontainebleau, false for the V scale, null when <c>gradeScale</c> was not sent.</param>
/// <param name="Notifications">Per notification type: true to receive it, false to opt out.</param>
public sealed record ProfilePatch(
    bool SetsDisplayName,
    string? DisplayName,
    bool SetsHomeWall,
    Guid? HomeWallId,
    bool? PreferFontGrades,
    IReadOnlyDictionary<NotificationType, bool> Notifications)
{
    /// <summary>The longest display name the web's field accepts.</summary>
    public const int MaxDisplayNameLength = 256;

    /// <summary>The grade scale names on the wire.</summary>
    public const string FontScale = "font";

    /// <summary>The V scale's name on the wire.</summary>
    public const string VScale = "v";

    /// <summary>Reads <paramref name="body"/>, or names the first thing wrong with it.</summary>
    public static (ProfilePatch? Patch, string? Error) Parse(JsonElement body)
    {
        if (body.ValueKind != JsonValueKind.Object)
        {
            return (null, "The body must be a JSON object.");
        }

        var patch = new ProfilePatch(false, null, false, null, null, new Dictionary<NotificationType, bool>());
        foreach (var property in body.EnumerateObject())
        {
            var (next, error) = Apply(patch, property);
            if (error is not null)
            {
                return (null, error);
            }

            patch = next!;
        }

        return (patch, null);
    }

    private static (ProfilePatch? Patch, string? Error) Apply(ProfilePatch patch, JsonProperty property) =>
        property.Name switch
        {
            "displayName" => ReadDisplayName(patch, property.Value),
            "homeWallId" => ReadHomeWall(patch, property.Value),
            "gradeScale" => ReadGradeScale(patch, property.Value),
            "notifications" => ReadNotifications(patch, property.Value),
            _ => (null, $"Unknown field '{property.Name}'."),
        };

    private static (ProfilePatch? Patch, string? Error) ReadDisplayName(ProfilePatch patch, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Null)
        {
            return (patch with { SetsDisplayName = true, DisplayName = null }, null);
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            return (null, "displayName must be a string or null.");
        }

        var name = value.GetString()!;
        return name.Trim().Length > MaxDisplayNameLength
            ? (null, $"displayName must be at most {MaxDisplayNameLength} characters.")
            : (patch with { SetsDisplayName = true, DisplayName = name }, null);
    }

    private static (ProfilePatch? Patch, string? Error) ReadHomeWall(ProfilePatch patch, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Null)
        {
            return (patch with { SetsHomeWall = true, HomeWallId = null }, null);
        }

        return value.ValueKind == JsonValueKind.String && Guid.TryParse(value.GetString(), out var wallId)
            ? (patch with { SetsHomeWall = true, HomeWallId = wallId }, null)
            : (null, "homeWallId must be a wall id or null.");
    }

    private static (ProfilePatch? Patch, string? Error) ReadGradeScale(ProfilePatch patch, JsonElement value)
    {
        var scale = value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        return scale switch
        {
            FontScale => (patch with { PreferFontGrades = true }, null),
            VScale => (patch with { PreferFontGrades = false }, null),
            _ => (null, "gradeScale must be \"font\" or \"v\"."),
        };
    }

    private static (ProfilePatch? Patch, string? Error) ReadNotifications(ProfilePatch patch, JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            return (null, "notifications must be an object of type names to true or false.");
        }

        var settings = new Dictionary<NotificationType, bool>(patch.Notifications);
        foreach (var entry in value.EnumerateObject())
        {
            if (!ProfileNotifications.TryParse(entry.Name, out var type))
            {
                return (null, $"Unknown notification type '{entry.Name}'.");
            }

            if (entry.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                return (null, $"notifications.{entry.Name} must be true or false.");
            }

            settings[type] = entry.Value.GetBoolean();
        }

        return (patch with { Notifications = settings }, null);
    }
}
