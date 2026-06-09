namespace Blocwerk.Core.Helpers;

public enum VolumeMode
{
    PyramidByDimensions,
    PyramidByAngle,
    Diamond,
}

public record Point2D(double X, double Y);

public record Point3D(double X, double Y, double Z);

public record VolumePiece(
    string Name,
    Point2D[] FlatVertices,
    double[] EdgeBevelAngles,
    double[] EdgeLengths);

public record VolumeResult(
    List<VolumePiece> Pieces,
    double DihedralAngleDeg,
    double BaseBevelAngleDeg,
    double MiterAngleDeg,
    double SlantHeight,
    double Apothem,
    Point3D[] BaseVertices,
    Point3D Apex,
    Point3D? BottomApex);
