// <copyright file="PhotoDistanceParser.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Globalization;
using System.Text.RegularExpressions;

namespace Blocwerk.Web.Components.Shared.MarkerPlanner;

/// <summary>
/// Reads the photo distance the owner types: "2500", "2500 mm", "250 cm", "2.5 m", "2,5m". A bare
/// number below <see cref="BareMetresBelow"/> is taken as metres — nobody photographs a wall from 3 mm,
/// and "2.5" is the natural way to type two and a half metres.
/// </summary>
public static partial class PhotoDistanceParser
{
    /// <summary>Bare numbers below this are metres, at or above it millimetres.</summary>
    public const double BareMetresBelow = 30;

    /// <summary>The distance in mm, or null when the text is not a positive length.</summary>
    public static double? ParseMm(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var match = DistancePattern().Match(text.Trim());
        if (!match.Success)
        {
            return null;
        }

        var number = match.Groups["n"].Value.Replace(',', '.');
        if (!double.TryParse(number, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value) || value <= 0)
        {
            return null;
        }

        var factor = match.Groups["u"].Value.ToLowerInvariant() switch
        {
            "m" => 1000.0,
            "cm" => 10.0,
            "mm" => 1.0,
            _ => value < BareMetresBelow ? 1000.0 : 1.0,
        };
        return Math.Round(value * factor, 1);
    }

    /// <summary>How the editor shows a distance back: metres with up to two decimals.</summary>
    public static string Format(double distanceMm) =>
        (distanceMm / 1000.0).ToString("0.##", CultureInfo.InvariantCulture) + " m";

    [GeneratedRegex(@"^(?<n>\d+(?:[.,]\d+)?)\s*(?<u>mm|cm|m)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DistancePattern();
}
