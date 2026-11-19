// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Entities;

namespace Blocwerk.Core.Services;

/// <summary>The one rule for a boulder's free-text custom rule: trimmed, empty is none, never longer than the column.</summary>
public static class BoulderCustomRule
{
    /// <summary>Normalises a custom rule for storage.</summary>
    /// <param name="text">What the setter typed, or null.</param>
    /// <returns>The trimmed text, or null when it is empty.</returns>
    /// <exception cref="InvalidOperationException">The trimmed text is longer than <see cref="Boulder.CustomRuleMaxLength"/>.</exception>
    public static string? Normalize(string? text)
    {
        var trimmed = text?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        if (trimmed.Length > Boulder.CustomRuleMaxLength)
        {
            throw new InvalidOperationException($"A custom rule can be at most {Boulder.CustomRuleMaxLength} characters long");
        }

        return trimmed;
    }
}
