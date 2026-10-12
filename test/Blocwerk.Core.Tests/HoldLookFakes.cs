// <copyright file="HoldLookFakes.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Tests;

/// <summary>
/// A presence probe that answers from a script instead of pixels (OpenCV is not available on every test machine): every
/// spot scores <see cref="Default"/> unless <see cref="Set"/> gave it a sequence of scores, one per time it is asked.
/// Spots are normalized coordinates on the new photo, which is <see cref="Width"/> by <see cref="Height"/> pixels.
/// </summary>
internal sealed class ScriptedPresenceProbe : IHoldPresenceProbe
{
    public const int Width = 64;
    public const int Height = 48;

    private readonly List<(double X, double Y, double?[] Scores, int[] Asked)> spots = [];

    /// <summary>The score of every spot nothing was scripted for.</summary>
    public double Default { get; set; } = 0.9;

    /// <summary>When set, every call fails.</summary>
    public bool Throws { get; set; }

    public int Calls { get; private set; }

    /// <summary>Scripts the spot: the n-th time it is asked it scores the n-th value, the last one from then on.</summary>
    public void Set(double x, double y, params double?[] scores)
    {
        spots.RemoveAll(s => Same(s.X, s.Y, x, y));
        spots.Add((x, y, scores, [0]));
    }

    public IReadOnlyList<double?> Score(byte[] oldImage, byte[] newImage, IReadOnlyList<PresenceQuery> queries)
    {
        Calls++;
        if (Throws)
        {
            throw new InvalidOperationException("The probe is broken.");
        }

        return queries.Select(ScoreOne).ToList();
    }

    private static bool Same(double ax, double ay, double bx, double by) => Math.Abs(ax - bx) < 0.01 && Math.Abs(ay - by) < 0.01;

    private double? ScoreOne(PresenceQuery q)
    {
        foreach (var spot in spots)
        {
            if (Same(spot.X, spot.Y, q.NewX / Width, q.NewY / Height))
            {
                var index = Math.Min(spot.Asked[0]++, spot.Scores.Length - 1);
                return spot.Scores[index];
            }
        }

        return Default;
    }
}

/// <summary>
/// Pairs holds that sit on the same spot (within 0.01), like <see cref="PositionHoldMatcher"/>, and also predicts every
/// old hold at its old coordinates: the two photos show the wall from the same place.
/// </summary>
internal sealed class SameFrameHoldMatcher : IHoldOverlapMatcher
{
    public HoldOverlapResult Match(
        byte[] leftImage,
        IReadOnlyList<MatcherHold> leftHolds,
        byte[] rightImage,
        IReadOnlyList<MatcherHold> rightHolds,
        HoldOverlapDirection direction,
        ILogger? diag = null,
        HoldOverlapSeed? seed = null)
    {
        var paired = new PositionHoldMatcher().Match(leftImage, leftHolds, rightImage, rightHolds, direction, diag, seed);
        return paired with { WarpedLeftPositions = leftHolds.Select(l => ((double X, double Y)?)(l.X, l.Y)).ToList() };
    }
}
