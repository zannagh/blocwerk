using System.Globalization;

namespace Blocwerk.Core.Helpers;

/// <summary>
/// A single flat panel to cut: a 2D profile (outer face outline) extruded by the
/// wood thickness, with a length and a fold/bevel angle per edge. Both the volume
/// and the wedge calculators reduce to a list of these for the cutting-plan PDF.
///
/// <see cref="EdgeInsetMm"/> is the horizontal offset between the top (outer) face
/// edge and the bottom (back) face edge caused by the bevel over the wood thickness:
/// positive means the back face is pulled in (undercut), negative pushed out. A
/// square edge is 0. This is what makes the beveled faces visible in the drawings.
/// </summary>
public sealed record CutPiece(
    string Name,
    int Quantity,
    Point2D[] Profile,
    double[] EdgeLengths,
    double[] EdgeBevelAngles,
    double Thickness,
    double[] EdgeInsetMm);

/// <summary>
/// Turns calculator results into a de-duplicated list of <see cref="CutPiece"/>
/// (identical pieces collapsed to a single entry with a quantity).
/// </summary>
public static class CuttingPlan
{
    public static IReadOnlyList<CutPiece> FromVolume(VolumeResult result, double thickness)
    {
        var grouped = new List<CutPiece>();
        var counts = new Dictionary<string, int>();
        var index = new Dictionary<string, int>();

        foreach (var piece in result.Pieces)
        {
            var key = Key(piece.Name, piece.EdgeLengths, piece.EdgeBevelAngles);
            if (counts.TryGetValue(key, out var c))
            {
                counts[key] = c + 1;
                grouped[index[key]] = grouped[index[key]] with { Quantity = c + 1 };
                continue;
            }

            counts[key] = 1;
            index[key] = grouped.Count;
            var inset = piece.EdgeBevelAngles.Select(a => VolumeInset(thickness, a)).ToArray();
            grouped.Add(new CutPiece(piece.Name, 1, piece.FlatVertices, piece.EdgeLengths, piece.EdgeBevelAngles, thickness, inset));
        }

        return grouped;
    }

    public static IReadOnlyList<CutPiece> FromWedge(WedgeResult result, double thickness) =>
        result.Pieces
            .Select(p =>
            {
                // The end panels are flat triangles sawn square through the board; the
                // face/lower panels carry the fold bevels on their cross-cut ends.
                var square = p.Name.StartsWith("Side", StringComparison.Ordinal);
                var inset = p.EdgeBevelAngles.Select(a => square ? 0.0 : WedgeInset(thickness, a)).ToArray();
                return new CutPiece(p.Name, p.Quantity, p.FlatVertices, p.EdgeLengths, p.EdgeBevelAngles, thickness, inset);
            })
            .ToList();

    // Volume pieces store the saw tilt as (90° - rake); the cut plane meets the board
    // face at (90° - stored), so the top/bottom offset over the thickness is t·tan(stored).
    private static double VolumeInset(double t, double bevelDeg)
    {
        var b = Math.Clamp(bevelDeg, 0, 80);
        return Math.Clamp(t * Math.Tan(b * Math.PI / 180.0), -3.5 * t, 3.5 * t);
    }

    // Wedge panels store the fold (included) angle between adjoining panels, which is
    // the angle the cut plane makes with the board face, so the offset is t / tan(fold).
    private static double WedgeInset(double t, double foldDeg)
    {
        if (foldDeg <= 0.1)
        {
            return 3.5 * t;
        }

        return Math.Clamp(t / Math.Tan(foldDeg * Math.PI / 180.0), -3.5 * t, 3.5 * t);
    }

    private static string Key(string name, double[] lengths, double[] bevels) =>
        name + "|" +
        string.Join(",", lengths.Select(v => Math.Round(v, 1).ToString(CultureInfo.InvariantCulture))) + "|" +
        string.Join(",", bevels.Select(v => Math.Round(v, 1).ToString(CultureInfo.InvariantCulture)));
}
