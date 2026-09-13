using Blocwerk.HoldDetection.Matching;

namespace Blocwerk.HoldDetection.Tests;

/// <summary>
/// Unit tests for the matcher's colour sanity-gate. The gate decision (<see cref="ColorGate.Rejects"/>)
/// is exactly what <c>ScoreAndAssign</c> consults before a candidate is added to the greedy pool:
/// a true result means the candidate is skipped (<c>continue</c>) and therefore never proposed, while
/// a false result lets an otherwise-good geometric match proceed. The distance fed to the gate is
/// produced by the same <see cref="LabMath.Distance"/> (quantile-mapped Lab) the matcher uses, so
/// these tests exercise the real gate path rather than a re-implementation.
/// </summary>
public class ColorGateTests
{
    // Blue hold (OpenCV 8-bit Lab). A rechalked / differently-lit version of the SAME blue hold sits
    // close in Lab; a brown marker-on-bare-wood hold sits far away — the classic mispair the gate kills.
    private static readonly double[] BlueHold = { 80, 120, 90 };
    private static readonly double[] BlueHoldRelit = { 95, 122, 88 };
    private static readonly double[] BrownWoodHold = { 150, 150, 180 };

    [Fact]
    public void DifferentColour_GeometricCandidate_IsRejected()
    {
        var qmap = IdentityMap();
        double dcol = LabMath.Distance(BlueHold, BrownWoodHold, qmap);

        Assert.True(dcol > ColorGate.ColorGateDeltaE, $"expected a clear colour mismatch, ΔE={dcol:F1}");
        Assert.True(ColorGate.Rejects(BlueHold, BrownWoodHold, dcol));
    }

    [Fact]
    public void SameColour_GeometricCandidate_IsNotRejected()
    {
        var qmap = IdentityMap();
        double dcol = LabMath.Distance(BlueHold, BlueHold, qmap);

        Assert.True(dcol <= ColorGate.ColorGateDeltaE);
        Assert.False(ColorGate.Rejects(BlueHold, BlueHold, dcol));
    }

    [Fact]
    public void RelitSameColour_StaysWithinGate()
    {
        var qmap = IdentityMap();
        double dcol = LabMath.Distance(BlueHold, BlueHoldRelit, qmap);

        // Lenient by design: a rechalked / differently-lit hold of the same colour must still match.
        Assert.True(dcol <= ColorGate.ColorGateDeltaE, $"a relit same-colour hold must survive the gate, ΔE={dcol:F1}");
        Assert.False(ColorGate.Rejects(BlueHold, BlueHoldRelit, dcol));
    }

    [Fact]
    public void MissingColour_IsNeverRejected()
    {
        // A colour-less hold must never be rejected on colour, no matter the distance argument.
        Assert.False(ColorGate.Rejects(null, BrownWoodHold, 999.0));
        Assert.False(ColorGate.Rejects(BlueHold, null, 999.0));
        Assert.False(ColorGate.Rejects(null, null, 999.0));
    }

    // Quantile map built from identical marginals on both sides, so Apply is effectively identity and
    // the gate sees the plain Lab distance — mirroring a pair with no exposure/white-balance drift.
    private static ColorQuantileMap IdentityMap()
    {
        var marginals = new List<double[]> { BlueHold, BrownWoodHold };
        return new ColorQuantileMap(marginals, marginals);
    }
}
