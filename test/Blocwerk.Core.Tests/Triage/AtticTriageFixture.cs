// <copyright file="AtticTriageFixture.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Text.Json;
using Blocwerk.Core.Services;

namespace Blocwerk.Core.Tests.Triage;

/// <summary>One labelled unpaired detection of the fixture (RAW pixels) and its precomputed presence score.</summary>
public sealed record LabelledCandidate(Guid Id, double X, double Y, bool Real, double? Presence);

/// <summary>One staged panel of the Attic panel-update fixture.</summary>
public sealed record AtticPanel(
    (int Width, int Height) Size,
    (int Width, int Height) OldSize,
    IReadOnlyList<LabelledCandidate> Candidates,
    IReadOnlyList<PointPair> Pairs,
    IReadOnlyList<IReadOnlyList<(double X, double Y)>> Markers,
    IReadOnlyList<PointPair> OverlapToMain);

/// <summary>
/// Reads <c>Triage/panel-update-attic.json</c>: The Attic's big update of 2026-09-29 before the operator's clean-up.
/// Per panel: every unpaired staged detection labelled real (4 on the centre, 1 on the oblique right panel)
/// or junk (deleted by hand, or accepted as discarded), the matched staged→old pairs, the printed markers
/// (stored observations plus the brightened marker pass), the presence probe's score for each detection
/// against the old photo, and for the right panel its overlap pairs onto the centre photo.
/// </summary>
public static class AtticTriageFixture
{
    public static IReadOnlyDictionary<string, AtticPanel> Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Triage", "panel-update-attic.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        return doc.RootElement.GetProperty("panels").EnumerateArray()
            .ToDictionary(p => p.GetProperty("name").GetString()!, ReadPanel);
    }

    private static AtticPanel ReadPanel(JsonElement p)
    {
        var candidates = p.GetProperty("candidates").EnumerateArray()
            .Select((c, i) => new LabelledCandidate(
                Id(i),
                c[0].GetDouble(),
                c[1].GetDouble(),
                c[2].GetInt32() == 1,
                c[3].ValueKind == JsonValueKind.Null ? null : c[3].GetDouble()))
            .ToList();
        var markers = p.GetProperty("markers").EnumerateArray()
            .Select(q => (IReadOnlyList<(double X, double Y)>)q.EnumerateArray().Select(c => (c[0].GetDouble(), c[1].GetDouble())).ToList())
            .ToList();
        var overlap = p.TryGetProperty("overlapToMain", out var o) ? Pairs(o) : [];
        return new AtticPanel(Size(p, "size"), Size(p, "oldSize"), candidates, Pairs(p.GetProperty("pairs")), markers, overlap);
    }

    private static List<PointPair> Pairs(JsonElement e) =>
        e.EnumerateArray().Select(q => new PointPair(q[0].GetDouble(), q[1].GetDouble(), q[2].GetDouble(), q[3].GetDouble())).ToList();

    private static (int Width, int Height) Size(JsonElement p, string name) =>
        (p.GetProperty(name)[0].GetInt32(), p.GetProperty(name)[1].GetInt32());

    private static Guid Id(int index) => new(index + 1, 0, 0, new byte[8]);
}
