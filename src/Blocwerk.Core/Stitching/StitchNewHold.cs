namespace Blocwerk.Core.Stitching;

/// <summary>A detection on the new master that no existing hold claimed.</summary>
public sealed record StitchNewHold(
    string DetectionId,
    double X,
    double Y,
    double Radius,
    double Confidence,
    bool LikelyDuplicate);
