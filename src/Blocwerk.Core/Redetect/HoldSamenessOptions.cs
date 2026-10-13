// <copyright file="HoldSamenessOptions.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.Redetect;

/// <summary>
/// Thresholds for judging whether two outlines on one photo are the same physical hold (a fresh detection against a
/// stored hold, or two stored holds). Bound from the <c>HoldSameness</c> configuration section; the defaults are
/// conservative: when in doubt two holds stay two holds.
/// </summary>
public sealed class HoldSamenessOptions
{
    /// <summary>The configuration section name.</summary>
    public const string Section = "HoldSameness";

    /// <summary>
    /// The larger hold may be at most this many times the smaller one, measured across (the square root of the area
    /// ratio). Beyond it the two are never the same hold: a small hold on a volume is not the volume.
    /// </summary>
    public double MaxSizeRatio { get; set; } = 1.8;

    /// <summary>
    /// Beyond <see cref="MaxSizeRatio"/>, a smaller hold that lies inside the larger one by at least this share of its
    /// own area sits ON it (stacked) and stays its own hold.
    /// </summary>
    public double StackedContainment { get; set; } = 0.6;

    /// <summary>
    /// The overlap score from which two outlines are in the same place. The score is the larger of the shared area over
    /// the combined area (IoU) and, when the smaller one lies inside the larger, its share inside divided by the size ratio.
    /// </summary>
    public double MinScore { get; set; } = 0.4;

    /// <summary>
    /// Two centres at most this far apart, as a fraction of the smaller hold's radius, are in the same place even when
    /// the outlines overlap less than <see cref="MinScore"/>.
    /// </summary>
    public double MaxCentreOffset { get; set; } = 0.35;

    /// <summary>From this score two stored holds of the same colour are merged without asking.</summary>
    public double SureScore { get; set; } = 0.7;

    /// <summary>The score two stored holds need to be merged without asking when a colour is unknown or only close.</summary>
    public double SureScoreUnknownColor { get; set; } = 0.85;

    /// <summary>
    /// A stored hold that is only a circle says "there is a hold here", not how large it is: a traced detection around
    /// its centre is that hold while the two are at most this far apart in size, measured across.
    /// </summary>
    public double MaxPlaceholderSizeRatio { get; set; } = 5;

    /// <summary>The detector confidence from which an outline for a plain circle can be accepted together with the others.</summary>
    public double SafeDetectorConfidence { get; set; } = 0.5;

    /// <summary>A detection whose outline matches the stored one at least this well (IoU) changes nothing.</summary>
    public double UnchangedIou { get; set; } = 0.9;
}
