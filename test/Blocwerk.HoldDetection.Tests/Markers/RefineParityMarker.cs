using System.Text.Json;
using Blocwerk.Core.Abstractions;

namespace Blocwerk.HoldDetection.Tests.Markers;

/// <summary>
/// One entry of Fixtures/Glyphs/refine-parity.json: the raw ArUco corners of a marker in a crop and
/// what the Python reference (tools/glyph/geometry/refine.py) refined them to, with its ok flags.
/// </summary>
internal sealed record RefineParityMarker(int Id, MarkerPoint[] Raw, MarkerPoint[] Refined, bool[] Ok)
{
    /// <summary>All entries, keyed by crop file name.</summary>
    public static Dictionary<string, List<RefineParityMarker>> LoadAll(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        return doc.RootElement.GetProperty("images").EnumerateObject().ToDictionary(
            img => img.Name,
            img => img.Value.EnumerateArray().Select(From).ToList());
    }

    private static RefineParityMarker From(JsonElement e) => new(
        e.GetProperty("id").GetInt32(),
        Points(e.GetProperty("raw")),
        Points(e.GetProperty("refined")),
        e.GetProperty("ok").EnumerateArray().Select(b => b.GetBoolean()).ToArray());

    private static MarkerPoint[] Points(JsonElement e) =>
        e.EnumerateArray().Select(p => new MarkerPoint(p[0].GetDouble(), p[1].GetDouble())).ToArray();
}
