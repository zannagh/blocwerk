namespace Blocwerk.Core.Helpers;

public record WedgePiece(
    string Name,
    int Quantity,
    Point2D[] FlatVertices,
    double[] EdgeBevelAngles,
    double[] EdgeLengths);

public record WedgeResult(
    List<WedgePiece> Pieces,
    Point2D[] CrossSection,
    string[] CrossSectionLabels,
    double[] CrossSectionEdgeLengths,
    double[] CrossSectionEdgeAngles,
    double AngleChangeDeg,
    double? LowerPortionAngleDeg,
    double LowerPortionLengthMm,
    double? FaceToLowerFoldDeg,
    double? LowerToWallFoldDeg,
    double DepthMm,
    double WallFootprintMm,
    double OverallWidthMm);
