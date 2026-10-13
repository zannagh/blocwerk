// <copyright file="HoldLookCheck.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Globalization;
using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Enums;
using Blocwerk.Core.HoldMoves;

namespace Blocwerk.Core.Services;

/// <summary>
/// The matcher pairs an old hold with a detection mostly by where it is, so a hold that was swapped for another one on
/// the same spot is still paired. This judges the pair by its look instead: the new photo's patch at the detection
/// against the old photo around the old hold (<see cref="IHoldPresenceProbe"/>). The same probe at an unpaired old
/// hold's predicted spot tells a hold that is gone from one the detector merely missed. A score that could not be
/// measured is no verdict at all. Pure.
/// </summary>
public static class HoldLookCheck
{
    /// <summary>The verdict on a pair's correlation.</summary>
    /// <param name="score">The correlation.</param>
    /// <param name="options">The thresholds.</param>
    /// <returns>The verdict.</returns>
    public static HoldLook Judge(double score, HoldMoveOptions options)
    {
        if (score >= options.SameLookMinScore)
        {
            return HoldLook.Same;
        }

        return score >= options.ReplacedBelowScore ? HoldLook.Unsure : HoldLook.Replaced;
    }

    /// <summary>
    /// Whether a pair that does not look alike is two holds of clearly different size (<see cref="NewHoldTriage.SameHoldSizeFactor"/>):
    /// a hold and the volume it sits on, paired because the other one was not detected. Such a pair is taken apart
    /// without "another hold is here now": the old hold is judged by its own spot, the detection is a hold of its own.
    /// </summary>
    /// <param name="oldRadiusPx">The old hold's radius on the new photo.</param>
    /// <param name="twinRadiusPx">The detection's radius.</param>
    /// <returns>True when both sizes are known and clearly differ.</returns>
    public static bool OtherSize(double oldRadiusPx, double twinRadiusPx) =>
        oldRadiusPx > 0 && twinRadiusPx > 0
        && Math.Max(oldRadiusPx, twinRadiusPx) > NewHoldTriage.SameHoldSizeFactor * Math.Min(oldRadiusPx, twinRadiusPx);

    /// <summary>Whether an old hold with no twin is gone from its predicted spot; anything above the bar says nothing.</summary>
    /// <param name="score">The correlation at the spot.</param>
    /// <param name="options">The thresholds.</param>
    /// <returns>True when the spot confidently no longer shows the hold.</returns>
    public static bool LooksGone(double score, HoldMoveOptions options) => score <= options.GoneMaxScore;

    /// <summary>
    /// Whether the two holds' stored fingerprints say "the same kind of hold" (colour and shape). The patch correlation is
    /// not rotation-tolerant, so a hold that was only turned must not be called replaced.
    /// </summary>
    /// <param name="oldFingerprintJson">The old hold's fingerprint.</param>
    /// <param name="twinFingerprintJson">The detection's fingerprint.</param>
    /// <param name="options">The look-alike bar.</param>
    /// <returns>True when both exist and are alike.</returns>
    public static bool SameKind(string? oldFingerprintJson, string? twinFingerprintJson, HoldMoveOptions options) =>
        HoldFingerprint.FromJson(oldFingerprintJson) is { } a && HoldFingerprint.FromJson(twinFingerprintJson) is { } b
        && HoldFingerprint.Similarity(a, b) >= options.SimilarityMin;

    /// <summary>
    /// The probe query for a spot on the new photo against a spot on the old photo (RAW pixels), or null when the spot is
    /// outside the new photo or the local scale between the photos is unknown there.
    /// </summary>
    /// <param name="newToOld">The panel's matched pairs, new photo to old photo.</param>
    /// <param name="newSpot">The spot on the new photo.</param>
    /// <param name="oldSpot">The old hold's centre on the old photo.</param>
    /// <param name="newSize">The new photo's size.</param>
    /// <param name="radiusPx">The hold's radius on the new photo.</param>
    /// <returns>The query or null.</returns>
    public static PresenceQuery? Query(
        IReadOnlyList<PointPair> newToOld, (double X, double Y) newSpot, (double X, double Y) oldSpot, (int Width, int Height) newSize, double radiusPx)
    {
        if (newSpot.X < 0 || newSpot.Y < 0 || newSpot.X >= newSize.Width || newSpot.Y >= newSize.Height)
        {
            return null;
        }

        return LocalAffine.Scale(newToOld, newSpot.X, newSpot.Y) is { } scale and > 0
            ? new PresenceQuery(newSpot.X, newSpot.Y, oldSpot.X, oldSpot.Y, scale, radiusPx)
            : null;
    }

    /// <summary>
    /// The component of every linked hold: holds joined by a link are the same physical hold seen on several panels.
    /// </summary>
    /// <param name="links">The links.</param>
    /// <returns>Hold id to a representative of its component; a hold in no link is absent.</returns>
    public static Dictionary<Guid, Guid> Components(IEnumerable<(Guid A, Guid B)> links)
    {
        var parent = new Dictionary<Guid, Guid>();
        Guid Find(Guid id)
        {
            while (parent.TryGetValue(id, out var up) && up != id)
            {
                id = up;
            }

            return id;
        }

        foreach (var (a, b) in links)
        {
            var (rootA, rootB) = (Find(a), Find(b));
            parent[rootA] = rootA;
            parent[rootB] = rootA;
        }

        return parent.Keys.ToDictionary(id => id, Find);
    }

    /// <summary>A compact histogram of scores for the log: below 0, then bins of 0.1 from 0.0 to 0.9 (1.0 counts as 0.9).</summary>
    /// <param name="scores">The measured scores.</param>
    /// <returns>For example <c>&lt;0:1,0.0:0,0.1:4,…,0.9:120</c>.</returns>
    public static string Histogram(IEnumerable<double> scores)
    {
        var bins = new int[11];
        foreach (var score in scores)
        {
            bins[score < 0 ? 0 : 1 + Math.Min(9, (int)(score * 10))]++;
        }

        return string.Join(',', bins.Select((n, i) => i == 0 ? $"<0:{n}" : string.Create(CultureInfo.InvariantCulture, $"{(i - 1) / 10.0:0.0}:{n}")));
    }
}
