using System.Text.Json;
using System.Text.Json.Serialization;
using Blocwerk.Core.Detection.Outlines;

namespace Blocwerk.Core.Abstractions;

/// <summary>
/// A compact appearance descriptor of one physical hold — colour, size and shape — measured on its
/// outlined pixels. Position is deliberately NOT part of it: it exists to recognise a hold that was moved
/// elsewhere on the wall (see <see cref="HoldRelocationMatcher"/>). Stored as JSON (<see cref="ToJson"/>).
/// </summary>
/// <remarks>
/// Colours use the OpenCV 8-bit Lab convention shared with the overlap matcher (L 0..255, a/b offset
/// by 128). Pixel sizes are only comparable within ONE photo; across photos only the metric fields
/// (<see cref="WidthMm"/> etc.), filled in by a caller that knows the wall plane, compare sizes.
/// </remarks>
public sealed record HoldFingerprint
{
    /// <summary>Number of chromatic hue bins in <see cref="Histogram"/>; the neutral bin follows them.</summary>
    public const int HueBins = 12;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Gets the schema version, bumped if the meaning of a field changes.</summary>
    public int Version { get; init; } = 1;

    /// <summary>Gets the median L over the hold mask (OpenCV 8-bit, 0..255).</summary>
    public double L { get; init; }

    /// <summary>Gets the median a over the hold mask (OpenCV 8-bit, 128 = neutral).</summary>
    public double A { get; init; }

    /// <summary>Gets the median b over the hold mask (OpenCV 8-bit, 128 = neutral).</summary>
    public double B { get; init; }

    /// <summary>
    /// Gets the colour histogram: <see cref="HueBins"/> hue bins of 30° over the Lab a/b angle, then one
    /// neutral (low-chroma: white/grey/black) bin. Sums to 1.
    /// </summary>
    public double[] Histogram { get; init; } = new double[HueBins + 1];

    /// <summary>Gets the mask area in pixels of the source photo (not comparable across photos).</summary>
    public double AreaPx { get; init; }

    /// <summary>Gets the long/short side ratio of the minimum-area rectangle (≥ 1).</summary>
    public double Aspect { get; init; } = 1;

    /// <summary>Gets the long axis orientation in degrees, 0..180, image-up = 90. Not used for similarity
    /// (a moved hold may be rotated) but useful to a caller that knows it was not.</summary>
    public double OrientationDeg { get; init; }

    /// <summary>Gets the contour area divided by its convex-hull area (0..1).</summary>
    public double Solidity { get; init; } = 1;

    /// <summary>Gets the seven Hu moments, log-scaled as <c>-sign(h)·log10|h|</c>.</summary>
    public double[] Hu { get; init; } = new double[7];

    /// <summary>Gets the metric width (long side) in millimetres, when a caller with a plane mapping set it.</summary>
    public double? WidthMm { get; init; }

    /// <summary>Gets the metric height (short side) in millimetres, when known.</summary>
    public double? HeightMm { get; init; }

    /// <summary>Gets the metric area in square millimetres, when known.</summary>
    public double? AreaMm2 { get; init; }

    /// <summary>
    /// Similarity of two fingerprints in 0..1 (1 = indistinguishable). Weights are documented on
    /// <see cref="HoldFingerprintSimilarity"/>; sizes only count when BOTH carry millimetres.
    /// </summary>
    /// <param name="a">First fingerprint.</param>
    /// <param name="b">Second fingerprint.</param>
    /// <returns>The similarity, 0..1.</returns>
    public static double Similarity(HoldFingerprint a, HoldFingerprint b) => HoldFingerprintSimilarity.Compute(a, b);

    /// <summary>Parses a fingerprint stored with <see cref="ToJson"/>; null for null/blank/garbage input.</summary>
    /// <param name="json">The stored JSON.</param>
    /// <returns>The fingerprint, or null.</returns>
    public static HoldFingerprint? FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<HoldFingerprint>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Serializes to compact camelCase JSON (for <c>Hold.FingerprintJson</c>).</summary>
    /// <returns>The JSON.</returns>
    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);
}
