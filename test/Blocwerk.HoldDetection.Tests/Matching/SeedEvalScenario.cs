using System.Text.Json.Nodes;
using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Geometry;
using Blocwerk.HoldDetection.Outlines;

namespace Blocwerk.HoldDetection.Tests.Matching;

/// <summary>One photo of the offline seed evaluation: the image plus its ground-truth holds and markers.</summary>
internal sealed class SeedEvalPhoto
{
    public required string Name { get; init; }

    public required byte[] Image { get; init; }

    public required int Width { get; init; }

    public required int Height { get; init; }

    public required List<SeedEvalHold> Holds { get; init; }

    public required List<DetectedMarker> Markers { get; init; }

    public List<MatcherHold> MatcherHolds() => Holds.Select((h, i) => new MatcherHold(i, h.X, h.Y, h.R)).ToList();

    public SeedPhoto SeedPhoto() => new(Markers, Width, Height);

    public List<SeedHold> SeedHolds() => Holds.Select(h => new SeedHold(h.X, h.Y, h.Facet)).ToList();

    /// <summary>Wall-space view with fingerprints measured by the real outline service on this photo.</summary>
    public List<WallSpaceHold> WallSpace()
    {
        var service = new OpenCvHoldOutlineService();
        using var session = service.OpenSession(Image);
        return Holds.Select((h, i) =>
        {
            var fp = session.Outline(new HoldSeed(h.X, h.Y, h.R)).Fingerprint;
            return new WallSpaceHold(i, h.Facet, h.A, h.B, Math.Max(h.WidthMm, h.HeightMm), fp);
        }).ToList();
    }
}

/// <summary>A ground-truth hold: projected from the solved wall model into the photo (see seedeval/fixture.py).</summary>
internal sealed record SeedEvalHold(string Id, double X, double Y, double R, string Facet, double A, double B, double WidthMm, double HeightMm);

/// <summary>Loads the fixture written by <c>seedeval/fixture.py</c> and the glyph PNGs.</summary>
internal static class SeedEvalScenario
{
    public static SeedEvalPhoto Load(JsonNode fixture, string pngDir, string name)
    {
        var img = fixture["images"]![name]!;
        int w = (int)img["width"]!, h = (int)img["height"]!;
        var holds = img["holds"]!.AsArray().Select(n => new SeedEvalHold(
            (string)n!["id"]!, (double)n["x"]!, (double)n["y"]!, (double)n["r"]!, (string)n["facet"]!,
            (double)n["a"]!, (double)n["b"]!, (double)n["wmm"]!, (double)n["hmm"]!)).ToList();
        var markers = img["markers"]!.AsArray().Select(n => ToMarker(n!, w, h)).ToList();
        return new SeedEvalPhoto
        {
            Name = name,
            Image = File.ReadAllBytes(Path.Combine(pngDir, name + ".png")),
            Width = w,
            Height = h,
            Holds = holds,
            Markers = markers,
        };
    }

    /// <summary>Scores proposals against identity: correct / wrong / missed over holds present in both photos.</summary>
    public static (int Correct, int Wrong, int Missed, int Truth) Score(
        SeedEvalPhoto left, SeedEvalPhoto right, HoldOverlapResult result)
    {
        var rightIds = right.Holds.Select(h => h.Id).ToHashSet();
        int truth = left.Holds.Count(h => rightIds.Contains(h.Id));
        int correct = 0, wrong = 0;
        foreach (var p in result.Proposals)
        {
            if (left.Holds[p.LeftHoldId].Id == right.Holds[p.RightHoldId].Id)
            {
                correct++;
            }
            else
            {
                wrong++;
            }
        }

        return (correct, wrong, truth - correct, truth);
    }

    private static DetectedMarker ToMarker(JsonNode n, int w, int h)
    {
        var corners = n["corners"]!.AsArray()
            .Select(c => new MarkerPoint((double)c![0]!, (double)c[1]!))
            .ToList();
        return new DetectedMarker
        {
            Id = (int)n["id"]!,
            CornersPx = corners,
            CornersNormalized = corners.Select(c => new MarkerPoint(c.X / w, c.Y / h)).ToList(),
            SidePx = (double)n["side"]!,
            EdgeRatio = 1,
        };
    }
}
