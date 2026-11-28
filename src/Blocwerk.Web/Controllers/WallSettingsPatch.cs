// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Text.Json;

namespace Blocwerk.Web.Controllers;

/// <summary>
/// The body of <c>PATCH /api/v1/walls/{w}/settings</c>, read as a merge patch: a field that is absent (or null) stays as
/// it is, a field that is present is set, and <c>description: ""</c> clears the description. An unknown field or a value
/// of the wrong type is refused, so a typo never silently does nothing.
/// </summary>
/// <param name="Name">The new name, or null.</param>
/// <param name="Description">The new description ("" clears it), or null.</param>
/// <param name="Angle">The overhang angle, 0 to 90 degrees, or null.</param>
/// <param name="UnderMaintenance">Update mode on or off, or null.</param>
/// <param name="AllowAnonymousKiosk">Anonymous kiosk setting on or off, or null.</param>
/// <param name="AllowKioskKeyboardShortcuts">Kiosk keyboard shortcuts on or off, or null.</param>
public sealed record WallSettingsPatch(
    string? Name,
    string? Description,
    int? Angle,
    bool? UnderMaintenance,
    bool? AllowAnonymousKiosk,
    bool? AllowKioskKeyboardShortcuts)
{
    /// <summary>The longest wall name the database stores.</summary>
    public const int MaxNameLength = 256;

    /// <summary>The longest description the database stores.</summary>
    public const int MaxDescriptionLength = 1024;

    /// <summary>True when the patch changes the name, description or angle (one <c>UpdateWallAsync</c> call).</summary>
    public bool ChangesDetails => Name is not null || Description is not null || Angle is not null;

    /// <summary>Reads <paramref name="body"/>, or names the first thing wrong with it.</summary>
    public static (WallSettingsPatch? Patch, string? Error) Parse(JsonElement body)
    {
        if (body.ValueKind != JsonValueKind.Object)
        {
            return (null, "The body must be a JSON object.");
        }

        var patch = new WallSettingsPatch(null, null, null, null, null, null);
        foreach (var property in body.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.Null)
            {
                if (!IsKnown(property.Name))
                {
                    return (null, $"Unknown field '{property.Name}'.");
                }

                continue;
            }

            var (next, error) = Apply(patch, property);
            if (error is not null)
            {
                return (null, error);
            }

            patch = next!;
        }

        return (patch, null);
    }

    private static bool IsKnown(string name) =>
        name is "name" or "description" or "angle" or "underMaintenance" or "allowAnonymousKiosk" or "allowKioskKeyboardShortcuts";

    private static (WallSettingsPatch? Patch, string? Error) Apply(WallSettingsPatch patch, JsonProperty property) =>
        property.Name switch
        {
            "name" => ReadName(patch, property.Value),
            "description" => ReadDescription(patch, property.Value),
            "angle" => property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetInt32(out var angle) && angle is >= 0 and <= 90
                ? (patch with { Angle = angle }, null)
                : (null, "angle must be a whole number from 0 to 90."),
            "underMaintenance" => Flag(property.Value, "underMaintenance", v => patch with { UnderMaintenance = v }),
            "allowAnonymousKiosk" => Flag(property.Value, "allowAnonymousKiosk", v => patch with { AllowAnonymousKiosk = v }),
            "allowKioskKeyboardShortcuts" => Flag(property.Value, "allowKioskKeyboardShortcuts", v => patch with { AllowKioskKeyboardShortcuts = v }),
            _ => (null, $"Unknown field '{property.Name}'."),
        };

    private static (WallSettingsPatch? Patch, string? Error) ReadName(WallSettingsPatch patch, JsonElement value)
    {
        var name = value.ValueKind == JsonValueKind.String ? value.GetString()!.Trim() : null;
        if (string.IsNullOrEmpty(name))
        {
            return (null, "name must be a non-empty string.");
        }

        return name.Length > MaxNameLength
            ? (null, $"name must be at most {MaxNameLength} characters.")
            : (patch with { Name = name }, null);
    }

    private static (WallSettingsPatch? Patch, string? Error) ReadDescription(WallSettingsPatch patch, JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String)
        {
            return (null, "description must be a string; \"\" clears it.");
        }

        var description = value.GetString()!.Trim();
        return description.Length > MaxDescriptionLength
            ? (null, $"description must be at most {MaxDescriptionLength} characters.")
            : (patch with { Description = description }, null);
    }

    private static (WallSettingsPatch? Patch, string? Error) Flag(JsonElement value, string name, Func<bool, WallSettingsPatch> set) =>
        value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? (set(value.GetBoolean()), null)
            : (null, $"{name} must be true or false.");
}
