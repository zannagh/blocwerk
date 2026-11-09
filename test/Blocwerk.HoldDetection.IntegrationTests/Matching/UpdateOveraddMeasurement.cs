// <copyright file="UpdateOveraddMeasurement.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Text.Json.Nodes;
using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Geometry;
using Blocwerk.Core.Services;
using Blocwerk.HoldDetection.Matching;
using Xunit.Abstractions;

namespace Blocwerk.HoldDetection.Tests.Matching;

/// <summary>
/// Read-only harness on the real photos of The Attic's gen 5 → 6 update (never committed): <c>BLOCWERK_OVERADD_DIR</c>
/// holds <c>holds.json</c> (every staged and old hold of the centre and right panel, normalized, their marker observations
/// and the photo paths) and <c>update-overadd-attic.json</c> (the committed geometry fixture). Writes
/// <c>photo-evidence.json</c> next to them: the overlap pairs right → centre as the overlaps step proposes them, the
/// presence probe's score for every unpaired detection against the old photo, and the carryover matcher's proposals and
/// warp per panel. Those numbers are what the committed fixture carries, so the triage tests need no photos. Each match is
/// seeded from the shared markers, as the app seeds it on a glyph wall. Skipped otherwise.
/// </summary>
public class UpdateOveraddMeasurement(ITestOutputHelper output)
{
    private const int Width = 4032;
    private const int Height = 3024;

    [SkippableFact]
    public void Attic_PhotoEvidence_ForTheOveraddFixture()
    {
        var dir = Environment.GetEnvironmentVariable("BLOCWERK_OVERADD_DIR");
        Skip.If(string.IsNullOrEmpty(dir) || !File.Exists(Path.Combine(dir, "holds.json")), "Set BLOCWERK_OVERADD_DIR to the harness directory.");
        var holds = JsonNode.Parse(File.ReadAllText(Path.Combine(dir!, "holds.json")))!;
        var fixture = JsonNode.Parse(File.ReadAllText(Path.Combine(dir!, "update-overadd-attic.json")))!;
        byte[] Photo(string key) => File.ReadAllBytes(holds["photos"]![key]!.GetValue<string>());

        var result = new JsonObject
        {
            ["overlap"] = Overlap(holds, Photo),
            ["presence"] = Presence(fixture, Photo, ownRadius: false),
            ["presenceOwn"] = Presence(fixture, Photo, ownRadius: true),
            ["carry"] = new JsonObject
            {
                ["centre"] = Carry(holds, Photo, "centre"),
                ["right"] = Carry(holds, Photo, "right"),
            },
        };
        File.WriteAllText(Path.Combine(dir!, "photo-evidence.json"), result.ToJsonString());
        output.WriteLine($"overlap pairs {result["overlap"]!.AsArray().Count}, presence {result["presence"]!.AsObject().Count}");
    }

    private static List<(Guid Id, MatcherHold Hold)> Holds(JsonNode holds, string key) =>
        holds[key]!.AsArray().Select((h, i) => (
            Guid.Parse(h!["id"]!.GetValue<string>()),
            new MatcherHold(
                i,
                h["x"]!.GetValue<double>(),
                h["y"]!.GetValue<double>(),
                h["r"]!.GetValue<double>(),
                Shape: h["shape"] is JsonArray s ? s.Select(p => (p![0]!.GetValue<double>(), p[1]!.GetValue<double>())).ToList() : null)))
        .ToList();

    private static SeedPhoto Markers(JsonNode holds, string key) => new(
        holds["markers"]![key]!.AsArray().Select(m =>
        {
            var norm = m!["corners"]!.AsArray().Select(c => new MarkerPoint(c![0]!.GetValue<double>(), c[1]!.GetValue<double>())).ToList();
            return new DetectedMarker
            {
                Id = m["id"]!.GetValue<int>(),
                CornersNormalized = norm,
                CornersPx = norm.Select(c => new MarkerPoint(c.X * Width, c.Y * Height)).ToList(),
                SidePx = 0,
                EdgeRatio = 1,
            };
        }).ToList(),
        Width,
        Height);

    private (HoldOverlapResult Result, List<(Guid Id, MatcherHold Hold)> Left, List<(Guid Id, MatcherHold Hold)> Right) Match(
        JsonNode holds, Func<string, byte[]> photo, string left, string right, bool carry)
    {
        var (l, r) = (Holds(holds, left), Holds(holds, right));
        var seed = OverlapSeedBuilder.Build(Markers(holds, left), Markers(holds, right), null, l.Select(h => new SeedHold(h.Hold.X, h.Hold.Y, null)).ToList());
        var matcher = new OpenCvHoldOverlapMatcher();
        var (lh, rh, log) = (l.Select(h => h.Hold).ToList(), r.Select(h => h.Hold).ToList(), new TestOutputLogger(output));
        var result = carry
            ? matcher.MatchCarry(photo(left), lh, photo(right), rh, HoldOverlapDirection.Right, log, seed)
            : matcher.Match(photo(left), lh, photo(right), rh, HoldOverlapDirection.Right, log, seed);
        return (result, l, r);
    }

    /// <summary>The overlaps step's proposals as RAW pixel pairs, right (this panel) → centre (the owner), with confidence.</summary>
    private JsonArray Overlap(JsonNode holds, Func<string, byte[]> photo)
    {
        var (match, centre, right) = Match(holds, photo, "centreNew", "rightNew", carry: false);
        var pairs = new JsonArray();
        foreach (var p in match.Proposals)
        {
            var (c, r) = (centre[p.LeftHoldId].Hold, right[p.RightHoldId].Hold);
            pairs.Add(new JsonArray(Math.Round(r.X * Width, 1), Math.Round(r.Y * Height, 1), Math.Round(c.X * Width, 1), Math.Round(c.Y * Height, 1), p.Confidence));
        }

        return pairs;
    }

    /// <summary>
    /// The probe's score of every unpaired detection, asked the way the triage asks it (with the hold's radius when large), or
    /// (<paramref name="ownRadius"/>) always with the hold's own radius, as the swap check asks it.
    /// </summary>
    private static JsonObject Presence(JsonNode fixture, Func<string, byte[]> photo, bool ownRadius)
    {
        var scores = new JsonObject();
        var probe = new OpenCvHoldPresenceProbe();
        foreach (var panel in fixture["panels"]!.AsArray())
        {
            var name = panel!["name"]!.GetValue<string>();
            var pairs = panel["pairs"]!.AsArray()
                .Select(p => new PointPair(p![0]!.GetValue<double>(), p[1]!.GetValue<double>(), p[2]!.GetValue<double>(), p[3]!.GetValue<double>()))
                .ToList();
            var asked = new List<(string Id, PresenceQuery Query)>();
            foreach (var c in panel["candidates"]!.AsArray())
            {
                var (x, y, r) = (c!["x"]!.GetValue<double>(), c["y"]!.GetValue<double>(), c["r"]!.GetValue<double>());
                if (LocalAffine.Predict(pairs, x, y) is { } old)
                {
                    var scale = LocalAffine.Scale(pairs, x, y) ?? 1;
                    asked.Add((c["id"]!.GetValue<string>(), new PresenceQuery(x, y, old.X, old.Y, scale, ownRadius ? r : NewHoldTriage.ProbeRadiusPx(r))));
                }
            }

            var got = probe.Score(photo($"{name}Old"), photo($"{name}New"), asked.Select(a => a.Query).ToList());
            for (var i = 0; i < asked.Count; i++)
            {
                scores[asked[i].Id] = got[i] is { } s ? Math.Round(s, 3) : null;
            }
        }

        return scores;
    }

    /// <summary>The carryover matcher's proposals (old id, new id, confidence, residual) and the warp of every old hold.</summary>
    private JsonObject Carry(JsonNode holds, Func<string, byte[]> photo, string panel)
    {
        var (match, oldHolds, newHolds) = Match(holds, photo, $"{panel}Old", $"{panel}New", carry: true);
        var proposals = new JsonArray();
        foreach (var p in match.Proposals)
        {
            proposals.Add(new JsonArray(oldHolds[p.LeftHoldId].Id.ToString(), newHolds[p.RightHoldId].Id.ToString(), p.Confidence, p.ResidualPx));
        }

        var warp = new JsonObject();
        for (var i = 0; i < oldHolds.Count && i < (match.WarpedLeftPositions?.Count ?? 0); i++)
        {
            if (match.WarpedLeftPositions![i] is { } w)
            {
                warp[oldHolds[i].Id.ToString()] = new JsonArray(Math.Round(w.X, 5), Math.Round(w.Y, 5));
            }
        }

        return new JsonObject { ["proposals"] = proposals, ["warp"] = warp };
    }
}
