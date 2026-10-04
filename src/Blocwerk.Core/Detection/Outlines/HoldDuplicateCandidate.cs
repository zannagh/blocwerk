namespace Blocwerk.Core.Detection.Outlines;

/// <summary>Why two holds on one photo look like the same physical hold.</summary>
public enum HoldDuplicateKind
{
    /// <summary>An automatic hold whose centre lies inside a hand-placed or hand-drawn hold.</summary>
    InsideHandPlaced = 0,

    /// <summary>Two automatic detections that overlap heavily and have a similar colour.</summary>
    NearDuplicateAutomatic = 1,

    /// <summary>Two hand-placed or hand-drawn holds that overlap almost completely.</summary>
    HandPlacedPair = 2,
}

/// <summary>What a merge keeps.</summary>
public enum HoldMergeMode
{
    /// <summary>Keep the suggested hold as it is (<see cref="HoldDuplicateCandidate.HoldA"/>, "left").</summary>
    KeepLeft = 0,

    /// <summary>Keep the other hold as it is ("right").</summary>
    KeepRight = 1,

    /// <summary>Keep the hand-made hold's identity but use the detected hold's position, outline and size.</summary>
    KeepHandUseDetectedShape = 2,
}

/// <summary>One suggested duplicate pair on a photo.</summary>
/// <param name="HoldA">The hold the merge rules would keep (see <see cref="HoldMergeRules"/>).</param>
/// <param name="HoldB">The hold they would remove.</param>
/// <param name="Kind">Why the pair was flagged.</param>
/// <param name="Confidence">0..1, how sure the detection is; the list is ordered by it.</param>
/// <param name="Iou">Shared area over combined area.</param>
/// <param name="Containment">Shared area over the smaller hold's area.</param>
public sealed record HoldDuplicateCandidate(
    Guid HoldA, Guid HoldB, HoldDuplicateKind Kind, double Confidence, double Iou, double Containment)
{
    /// <summary>The pre-selected merge: a hand-made hold against a detection keeps the hand-made hold with the detected shape.</summary>
    public HoldMergeMode SuggestedMode => Kind == HoldDuplicateKind.InsideHandPlaced
        ? HoldMergeMode.KeepHandUseDetectedShape
        : HoldMergeMode.KeepLeft;

    /// <summary>The pair as a stable key (lower id first), for remembering a dismissal.</summary>
    public (Guid, Guid) Key => HoldMergeRules.PairKey(HoldA, HoldB);
}
