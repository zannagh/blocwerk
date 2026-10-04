using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;

namespace Blocwerk.Core.Detection.Outlines;

/// <summary>The rules for merging two duplicate holds into one; pure, shared by the finder (who is "left") and the merge.</summary>
public static class HoldMergeRules
{
    /// <summary>True for a hold a person placed or drew: virtual, not automatic, or with a hand-edited outline.</summary>
    /// <param name="hold">The hold.</param>
    /// <returns>Whether it is hand-made.</returns>
    public static bool IsHandMade(Hold hold) => hold.IsVirtual || !hold.IsAutoDetected || hold.OutlineSource == HoldOutlineSource.Manual;

    /// <summary>The pair in a fixed order (lower id first), so a pair has one key whichever way round it is named.</summary>
    /// <param name="a">One hold.</param>
    /// <param name="b">The other.</param>
    /// <returns>The ordered pair.</returns>
    public static (Guid, Guid) PairKey(Guid a, Guid b) => a.CompareTo(b) <= 0 ? (a, b) : (b, a);

    /// <summary>
    /// The hold to keep: a hand-made one beats an automatic one, then the one more boulders use, then the larger
    /// footprint; the lower id settles a tie so the answer is stable.
    /// </summary>
    /// <param name="a">One hold.</param>
    /// <param name="countA">Boulders using <paramref name="a"/>.</param>
    /// <param name="b">The other hold.</param>
    /// <param name="countB">Boulders using <paramref name="b"/>.</param>
    /// <returns>The hold to keep.</returns>
    public static Hold PickKeeper(Hold a, int countA, Hold b, int countB)
    {
        if (IsHandMade(a) != IsHandMade(b))
        {
            return IsHandMade(a) ? a : b;
        }

        if (countA != countB)
        {
            return countA > countB ? a : b;
        }

        double areaA = ShapeGeometry.Area(HoldShapePolygon.Of(a));
        double areaB = ShapeGeometry.Area(HoldShapePolygon.Of(b));
        if (Math.Abs(areaA - areaB) > 1e-9)
        {
            return areaA > areaB ? a : b;
        }

        return a.Id.CompareTo(b.Id) <= 0 ? a : b;
    }

    /// <summary>
    /// The owner's default for a hand-made hold against an automatic detection at the same spot: the hand-made hold keeps
    /// its identity (boulders, name, flags) but takes the detection's position, outline and size, and a virtual hold
    /// becomes a real one.
    /// </summary>
    /// <param name="kept">The hand-made hold.</param>
    /// <param name="detected">The automatic detection that goes.</param>
    public static void AdoptDetectedShape(Hold kept, Hold detected)
    {
        kept.X = detected.X;
        kept.Y = detected.Y;
        kept.Radius = detected.Radius;
        kept.ShapePoints = detected.ShapePoints?.Select(p => new ShapePoint { Dx = p.Dx, Dy = p.Dy }).ToList();
        kept.ShapeHoles = ShapePoint.CloneRings(detected.ShapeHoles);
        kept.CopyGlyphMetricsFrom(detected);
        kept.IsVirtual = false;
    }

    /// <summary>The more prominent of two roles on one boulder: top over start over a plain hold.</summary>
    /// <param name="a">One role.</param>
    /// <param name="b">The other.</param>
    /// <returns>The role the merged hold gets.</returns>
    public static HoldType MergeType(HoldType a, HoldType b) => a >= b ? a : b;

    /// <summary>Both uses of a hold on one boulder combined: the same use stays, different uses become hand and foot.</summary>
    /// <param name="a">One use.</param>
    /// <param name="b">The other.</param>
    /// <returns>The merged use.</returns>
    public static HoldUsage MergeUsage(HoldUsage a, HoldUsage b) => a == b ? a : HoldUsage.HandAndFoot;

    /// <summary>
    /// Carries what is useful from <paramref name="removed"/> onto <paramref name="kept"/>: blanks (name, colour, material,
    /// grip type) are filled, the kickboard and review flags are kept if either had them, the confidence is the higher
    /// one, and a hold either side was hand-made is hand-made afterwards. The outline, position and measurements stay the keeper's.
    /// </summary>
    /// <param name="kept">The hold that stays.</param>
    /// <param name="removed">The hold that goes.</param>
    public static void MergeFlags(Hold kept, Hold removed)
    {
        kept.Name = string.IsNullOrWhiteSpace(kept.Name) ? removed.Name : kept.Name;
        kept.Color = string.IsNullOrWhiteSpace(kept.Color) ? removed.Color : kept.Color;
        kept.Material ??= removed.Material;
        kept.HandType ??= removed.HandType;
        kept.IsOnKickboard |= removed.IsOnKickboard;
        kept.NeedsReview |= removed.NeedsReview;
        kept.Confidence = Math.Max(kept.Confidence, removed.Confidence);
        kept.IsAutoDetected &= removed.IsAutoDetected;
    }
}
