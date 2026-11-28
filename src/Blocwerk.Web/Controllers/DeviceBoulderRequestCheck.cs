// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Helpers;

namespace Blocwerk.Web.Controllers;

/// <summary>
/// The companion app's setter bodies checked against what the web designer can produce, so every bad body is a
/// permanent 400 before the service runs (never a database error the queue would retry): a replay key, a picker grade,
/// the known styles and hold types, names, rules and the foot colour within their columns, and bounded, distinct lists
/// of holds, setters and hold types. The service decides what needs the database (membership, who may revise, holds on
/// the wall, variants).
/// </summary>
internal static class DeviceBoulderRequestCheck
{
    /// <summary>The longest boulder name the database stores.</summary>
    public const int MaxNameLength = 256;

    /// <summary>The longest foot colour the database stores.</summary>
    public const int MaxFootColorLength = 32;

    /// <summary>The most holds a boulder may be saved with.</summary>
    public const int MaxHolds = 200;

    /// <summary>The most setters a boulder may name.</summary>
    public const int MaxSetters = 20;

    /// <summary>The message when a write comes without its replay key.</summary>
    public const string ClientRequestIdRequired = "clientRequestId is required.";

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

        return KeyError(request.ClientRequestId)
            ?? FieldsError(request.Name, request.Grade, request.CustomRule, request.FootColorOnly, request.Styles)
            ?? HoldsError(request.Holds)
            ?? SettersError(request.Setters);
    }

    /// <summary>The first problem of a revise body, or null.</summary>
    public static string? ReviseError(DeviceReviseBoulderRequest request)
    {
        if (request.Name is not null && string.IsNullOrWhiteSpace(request.Name))
        {
            return "A boulder name must not be blank.";
        }

        return KeyError(request.ClientRequestId)
            ?? FieldsError(request.Name, request.Grade, request.CustomRule, request.FootColorOnly, request.Styles)
            ?? (request.Holds is null ? null : HoldsError(request.Holds))
            ?? (request.Setters is null ? null : SettersError(request.Setters))
            ?? HoldTypesError(request.HoldTypes);
    }

    private static string? KeyError(Guid? key) => key switch
    {
        null => ClientRequestIdRequired,
        { } id when id == Guid.Empty => Core.Services.ClientRequestIds.EmptyMessage,
        _ => null,
    };

    private static string? FieldsError(string? name, string? grade, string? customRule, string? footColor, int? styles)
    {
        if (name is not null && name.Trim().Length > MaxNameLength)
        {
            return $"A boulder name can be at most {MaxNameLength} characters long.";
        }

        if (!string.IsNullOrWhiteSpace(grade) && GradeScale.IsFont(grade.Trim()) is null)
        {
            return $"Unknown grade '{grade}'.";
        }

        if (customRule is not null && customRule.Trim().Length > Boulder.CustomRuleMaxLength)
        {
            return $"A custom rule can be at most {Boulder.CustomRuleMaxLength} characters long";
        }

        if (footColor is not null && footColor.Trim().Length > MaxFootColorLength)
        {
            return $"A foot colour can be at most {MaxFootColorLength} characters long.";
        }

        return styles is { } bits && (bits < 0 || ((BoulderStyle)bits & ~BoulderStyles.Mask) != 0)
            ? "Unknown style."
            : null;
    }

    private static string? HoldsError(List<BoulderHoldDto> holds)
    {
        if (holds.Count == 0)
        {
            return "Select at least one hold";
        }

        if (holds.Count > MaxHolds)
        {
            return $"A boulder can have at most {MaxHolds} holds.";
        }

        return holds.Select(h => h.HoldId).Distinct().Count() != holds.Count || holds.Any(h => h.HoldId == Guid.Empty)
            ? "Each hold may be listed once."
            : null;
    }

    private static string? SettersError(List<Guid> setters)
    {
        if (setters.Count > MaxSetters)
        {
            return $"A boulder can name at most {MaxSetters} setters.";
        }

        return setters.Distinct().Count() != setters.Count ? "Each setter may be listed once." : null;
    }

    private static string? HoldTypesError(List<string?>? names)
    {
        if (names is null)
        {
            return null;
        }

        if (names.Count > BoulderHoldTypes.All.Count)
        {
            return $"A boulder can have at most {BoulderHoldTypes.All.Count} hold types.";
        }

        return names.FirstOrDefault(n => string.IsNullOrWhiteSpace(n) || BoulderHoldTypes.FromSlug(n) is null) is { } unknown
            ? $"Unknown hold type '{unknown}'."
            : null;
    }
}
