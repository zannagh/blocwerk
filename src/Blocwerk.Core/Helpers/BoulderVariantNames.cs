// <copyright file="BoulderVariantNames.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Text.RegularExpressions;

namespace Blocwerk.Core.Helpers;

/// <summary>Suggests the name of a new variant from the boulder it starts from.</summary>
public static partial class BoulderVariantNames
{
    private const int MaxLength = 128;

    /// <summary>
    /// "Crimp Line Variant", then "Crimp Line Variant 2" and so on until the name is free on the wall. A variant of a
    /// variant counts on from the same base name.
    /// </summary>
    /// <param name="originName">The name of the boulder the variant starts from.</param>
    /// <param name="takenNames">The names already used on the wall.</param>
    /// <returns>The suggestion, at most 128 characters long.</returns>
    public static string Suggest(string originName, IEnumerable<string> takenNames)
    {
        var taken = takenNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var baseName = VariantSuffix().Replace(originName.Trim(), string.Empty);
        for (var number = 1; ; number++)
        {
            var suffix = number == 1 ? " Variant" : $" Variant {number}";
            var head = baseName.Length + suffix.Length > MaxLength ? baseName[..(MaxLength - suffix.Length)].TrimEnd() : baseName;
            if (!taken.Contains(head + suffix))
            {
                return head + suffix;
            }
        }
    }

    [GeneratedRegex(@"\s+Variant(\s+\d+)?$", RegexOptions.IgnoreCase)]
    private static partial Regex VariantSuffix();
}
