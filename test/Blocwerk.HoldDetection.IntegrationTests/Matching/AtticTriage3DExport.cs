// <copyright file="AtticTriage3DExport.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Text.Json;
using Blocwerk.Core.Services;

namespace Blocwerk.HoldDetection.Tests.Matching;

/// <summary>A hold row of the export (normalised position, placement and size).</summary>
internal sealed record ExportedHold(
    double X, double Y, int Generation, string? FacetId, double? PlaneAMm, double? PlaneBMm, string? MetricSource, double? WidthMm, double? HeightMm, bool IsVolume, int? PanelCol);

/// <summary>A hold proposal row of the export.</summary>
internal sealed record ExportedProposal(string FacetId, double A, double B, double SizeMm);

/// <summary>One labelled unpaired detection (RAW pixels) with its precomputed presence score.</summary>
internal sealed record AtticFixtureCandidate(Guid Id, double X, double Y, bool Real, double? Presence);

/// <summary>One panel of the Attic triage fixture (the same file the Core triage regression reads).</summary>
internal sealed record AtticFixturePanel(
    (int Width, int Height) Size,
    (int Width, int Height) OldSize,
    IReadOnlyList<AtticFixtureCandidate> Candidates,
    IReadOnlyList<PointPair> Pairs,
    IReadOnlyList<IReadOnlyList<(double X, double Y)>> Markers,
    IReadOnlyList<PointPair> OverlapToMain);

/// <summary>Reads <c>panel-update-attic.json</c>.</summary>
internal static class AtticFixturePanels
{
    public static IReadOnlyDictionary<string, AtticFixturePanel> Load(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        return doc.RootElement.GetProperty("panels").EnumerateArray()
            .ToDictionary(p => p.GetProperty("name").GetString()!, Read);
    }

    private static AtticFixturePanel Read(JsonElement p)
    {
        var candidates = p.GetProperty("candidates").EnumerateArray()
            .Select((c, i) => new AtticFixtureCandidate(
                new Guid(i + 1, 0, 0, new byte[8]),
                c[0].GetDouble(),
                c[1].GetDouble(),
                c[2].GetInt32() == 1,
                c[3].ValueKind == JsonValueKind.Null ? null : c[3].GetDouble()))
            .ToList();
        var markers = p.GetProperty("markers").EnumerateArray()
            .Select(q => (IReadOnlyList<(double X, double Y)>)q.EnumerateArray().Select(c => (c[0].GetDouble(), c[1].GetDouble())).ToList())
            .ToList();
        var overlap = p.TryGetProperty("overlapToMain", out var o) ? Pairs(o) : [];
        return new AtticFixturePanel(Size(p, "size"), Size(p, "oldSize"), candidates, Pairs(p.GetProperty("pairs")), markers, overlap);
    }

    private static List<PointPair> Pairs(JsonElement e) =>
        e.EnumerateArray().Select(q => new PointPair(q[0].GetDouble(), q[1].GetDouble(), q[2].GetDouble(), q[3].GetDouble())).ToList();

    private static (int Width, int Height) Size(JsonElement p, string name) =>
        (p.GetProperty(name)[0].GetInt32(), p.GetProperty(name)[1].GetInt32());
}
