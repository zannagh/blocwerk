// <copyright file="PrintOptions.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.MarkerPlanning;

/// <summary>How the PDF prints the markers.</summary>
/// <param name="MountingHoles">Screw holes printed around every marker; null = none.</param>
public sealed record PrintOptions(MountingHoles? MountingHoles)
{
    /// <summary>True when the plan asks for mounting holes.</summary>
    public bool HolesEnabled => MountingHoles is { Enabled: true };
}

/// <summary>
/// Screw holes printed diagonally outward from each corner of every marker's black square, tight to it:
/// the screw head's rim stays <paramref name="GapToMarkerMm"/> from the square and the cut line
/// <paramref name="GapToEdgeMm"/> beyond the head (see <see cref="MountingHoleLayout"/>).
/// </summary>
/// <param name="Enabled">Print the holes.</param>
/// <param name="HoleDiameterMm">Drilled/punched hole: one of <see cref="AllowedHoleDiametersMm"/>.</param>
/// <param name="ScrewHeadDiameterMm">Screw head diameter, <see cref="MinHeadOverHoleMm"/> over the hole up to <see cref="MaxHeadDiameterMm"/>.</param>
/// <param name="GapToMarkerMm">White gap between the black square's corner and the screw head's rim.</param>
/// <param name="GapToEdgeMm">Paper between the screw head's rim and the cut line.</param>
public sealed record MountingHoles(
    bool Enabled,
    double HoleDiameterMm,
    double ScrewHeadDiameterMm,
    double GapToMarkerMm = MountingHoles.DefaultGapMm,
    double GapToEdgeMm = MountingHoles.DefaultGapMm)
{
    /// <summary>Default for both gaps.</summary>
    public const double DefaultGapMm = 1;

    /// <summary>Smallest gap the layout accepts.</summary>
    public const double MinGapMm = 0.5;

    /// <summary>Largest gap the layout accepts.</summary>
    public const double MaxGapMm = 10;

    /// <summary>Largest screw head the layout accepts.</summary>
    public const double MaxHeadDiameterMm = 15;

    /// <summary>The head must be at least this much wider than the hole.</summary>
    public const double MinHeadOverHoleMm = 0.5;

    /// <summary>Default hole when the owner ticks the box (a 3 mm screw).</summary>
    public const double DefaultHoleDiameterMm = 3;

    /// <summary>Default screw head (6 mm, a typical 3 mm wood screw).</summary>
    public const double DefaultHeadDiameterMm = 6;

    /// <summary>The printable hole sizes.</summary>
    public static IReadOnlyList<double> AllowedHoleDiametersMm { get; } = [1, 2, 2.5, 3, 3.5];

    /// <summary>Holes on, 3 mm hole, 6 mm head.</summary>
    public static MountingHoles Default { get; } = new(true, DefaultHoleDiameterMm, DefaultHeadDiameterMm);

    /// <summary>True when <paramref name="mm"/> is one of <see cref="AllowedHoleDiametersMm"/>.</summary>
    public static bool IsAllowedHole(double mm) => AllowedHoleDiametersMm.Any(d => Math.Abs(d - mm) < 1e-9);

    /// <summary>Smallest head allowed for <paramref name="holeMm"/>.</summary>
    public static double MinHeadFor(double holeMm) => holeMm + MinHeadOverHoleMm;

    /// <summary>Readable problems with the sizes (empty when valid).</summary>
    public IReadOnlyList<string> Problems()
    {
        var problems = new List<string>();
        if (!double.IsFinite(HoleDiameterMm) || !IsAllowedHole(HoleDiameterMm))
        {
            problems.Add($"The mounting hole must be {string.Join(", ", AllowedHoleDiametersMm.Select(Format))} mm (was {Format(HoleDiameterMm)}).");
            return problems;
        }

        var min = MinHeadFor(HoleDiameterMm);
        if (!double.IsFinite(ScrewHeadDiameterMm) || ScrewHeadDiameterMm < min || ScrewHeadDiameterMm > MaxHeadDiameterMm)
        {
            problems.Add($"The screw head must be between {Format(min)} and {Format(MaxHeadDiameterMm)} mm for a {Format(HoleDiameterMm)} mm hole (was {Format(ScrewHeadDiameterMm)}).");
        }

        CheckGap(problems, "gap from the screw head to the marker", GapToMarkerMm);
        CheckGap(problems, "gap from the screw head to the cut edge", GapToEdgeMm);

        return problems;
    }

    /// <summary>True when <paramref name="mm"/> is an allowed gap.</summary>
    public static bool IsAllowedGap(double mm) => double.IsFinite(mm) && mm >= MinGapMm && mm <= MaxGapMm;

    private static void CheckGap(List<string> problems, string what, double mm)
    {
        if (!IsAllowedGap(mm))
        {
            problems.Add($"The {what} must be between {Format(MinGapMm)} and {Format(MaxGapMm)} mm (was {Format(mm)}).");
        }
    }

    private static string Format(double mm) => mm.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
}
