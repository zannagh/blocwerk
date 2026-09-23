// <copyright file="PlannerInput.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Globalization;

namespace Blocwerk.Web.Components.Shared.MarkerPlanner;

/// <summary>Reads the planner's number inputs (browser number fields send invariant text; a comma is tolerated).</summary>
public static class PlannerInput
{
    /// <summary>The finite number in <paramref name="value"/>, or null.</summary>
    public static double? Number(object? value)
    {
        var text = value?.ToString()?.Trim().Replace(',', '.');
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number)
            ? number
            : null;
    }

    /// <summary>The enum member named in <paramref name="value"/>, or null.</summary>
    /// <typeparam name="TEnum">The enum to parse into.</typeparam>
    public static TEnum? Enum<TEnum>(object? value)
        where TEnum : struct, System.Enum =>
        System.Enum.TryParse<TEnum>(value?.ToString(), ignoreCase: true, out var parsed) ? parsed : null;
}
