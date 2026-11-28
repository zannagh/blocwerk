// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Helpers;

namespace Blocwerk.Web.Controllers;

/// <summary>
/// The companion app's setter bodies checked against what the web designer can produce: a grade of its pickers, the
/// known styles and hold types, a name and custom rule within their columns. The service decides everything that needs
/// the database (membership, who may revise, holds on the wall, variants).
/// </summary>
internal static class DeviceBoulderRequestCheck
{
    /// <summary>The longest boulder name the database stores.</summary>
    public const int MaxNameLength = 256;

    /// <summary>The first problem of a create body, or null.</summary>
    public static string? CreateError(CreateBoulderRequest request, Guid wallId)
    {
        if (request.WallId != Guid.Empty && request.WallId != wallId)
        {
            return "wallId does not match the route.";
        }

        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return "A boulder name is required.";
        }

        return SharedError(request.Name, request.Grade, request.CustomRule, request.Styles)
            ?? (request.Holds.Count == 0 ? "Select at least one hold" : null);
    }

    /// <summary>The first problem of a revise body, or null.</summary>
    public static string? ReviseError(ReviseBoulderRequest request) =>
        request.Name is not null && string.IsNullOrWhiteSpace(request.Name)
            ? "A boulder name must not be blank."
            : SharedError(request.Name, request.Grade, request.CustomRule, request.Styles)
              ?? HoldTypesError(request.HoldTypes);

    private static string? SharedError(string? name, string? grade, string? customRule, int? styles)
    {
        if (name is not null && name.Trim().Length > MaxNameLength)
        {
            return $"A boulder name can be at most {MaxNameLength} characters long.";
        }

        if (!string.IsNullOrWhiteSpace(grade) && GradeScale.IsFont(grade) is null)
        {
            return $"Unknown grade '{grade}'.";
        }

        if (customRule is not null && customRule.Trim().Length > Boulder.CustomRuleMaxLength)
        {
            return $"A custom rule can be at most {Boulder.CustomRuleMaxLength} characters long";
        }

        return styles is { } bits && (bits < 0 || ((BoulderStyle)bits & ~BoulderStyles.Mask) != 0)
            ? "Unknown style."
            : null;
    }

    private static string? HoldTypesError(List<string?>? names) =>
        names?.FirstOrDefault(n => !string.IsNullOrEmpty(n) && BoulderHoldTypes.FromSlug(n) is null) is { } unknown
            ? $"Unknown hold type '{unknown}'."
            : null;
}
