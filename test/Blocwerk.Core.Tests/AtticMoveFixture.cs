// <copyright file="AtticMoveFixture.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Text.Json;
using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Geometry.View3D;
using Blocwerk.Core.Services;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The Attic's gen 6 → 7 update (run c7ced54a, 2026-10-26) as the move plan saw it, from
/// <c>test/real-data/attic/moves-endoct.json</c>: every carried pair with the old hold (its photo spot, facet placement,
/// size, apex and fingerprint axis), the staged detection (the same, as staged), and the matcher's warp spot. What the
/// run's summary listed as moved is in <see cref="MovedInRun"/>. Hand-placed merges (the plan skips them) and the hold that
/// really moved (<see cref="RealMoveId"/>, not paired in the run) are left out of <see cref="Pairs"/>.
/// </summary>
internal sealed class AtticMoveFixture
{
    /// <summary>The orange hold that really moved, about 1100 px across the centre panel.</summary>
    public const string RealMoveId = "c57a2626";

    private AtticMoveFixture(
        List<(Hold Old, Hold Twin)> pairs,
        Dictionary<Guid, HoldPositionNorm> warp,
        Dictionary<Guid, (int Width, int Height)> sizes,
        Dictionary<string, (double Mm, int Source, string Text, int Boulders)> moved)
    {
        (Pairs, Warp, Sizes, MovedInRun) = (pairs, warp, sizes, moved);
    }

    /// <summary>Gets the carried pairs the run measured.</summary>
    public List<(Hold Old, Hold Twin)> Pairs { get; }

    /// <summary>Gets the warp spot of each old hold on the new photo.</summary>
    public Dictionary<Guid, HoldPositionNorm> Warp { get; }

    /// <summary>Gets each staged panel photo's size.</summary>
    public Dictionary<Guid, (int Width, int Height)> Sizes { get; }

    /// <summary>Gets the run's moved holds by old id prefix: distance, source (0 = 3D, 1 = photo), text, boulders.</summary>
    public Dictionary<string, (double Mm, int Source, string Text, int Boulders)> MovedInRun { get; }

    /// <summary>Loads the fixture.</summary>
    /// <returns>The fixture.</returns>
    public static AtticMoveFixture Load()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(Dir(), "moves-endoct.json")));
        var root = doc.RootElement;
        var sizes = new Dictionary<Guid, (int Width, int Height)>();
        foreach (var panel in root.GetProperty("panels").EnumerateObject())
        {
            var s = panel.Value.GetProperty("newSize");
            sizes[PanelId(panel.Name)] = (s[0].GetInt32(), s[1].GetInt32());
        }

        var pairs = new List<(Hold, Hold)>();
        var warp = new Dictionary<Guid, HoldPositionNorm>();
        foreach (var p in root.GetProperty("pairs").EnumerateArray())
        {
            var oldId = p.GetProperty("old").GetString()!;
            if (p.GetProperty("hand").GetBoolean() || oldId.StartsWith(RealMoveId, StringComparison.Ordinal))
            {
                continue;
            }

            var panel = PanelId(p.GetProperty("panel").GetString()!);
            var old = Build(Guid.Parse(oldId), p.GetProperty("o"), p.GetProperty("oSize"), p.GetProperty("oFp"), panel);
            WithProtrusion(old, p.GetProperty("oProt"));
            var twin = Build(Guid.Parse(p.GetProperty("new").GetString()!), p.GetProperty("n"), p.GetProperty("nSize"), p.GetProperty("nFp"), panel);
            var w = p.GetProperty("warp");
            warp[old.Id] = new HoldPositionNorm(w[0].GetDouble(), w[1].GetDouble());
            pairs.Add((old, twin));
        }

        var moved = root.GetProperty("moved").EnumerateArray().ToDictionary(
            m => m.GetProperty("oldHoldId").GetString()![..8],
            m => (m.GetProperty("distanceMm").GetDouble(), m.GetProperty("source").GetInt32(), m.GetProperty("text").GetString()!, m.GetProperty("boulders").GetInt32()));
        return new AtticMoveFixture(pairs, warp, sizes, moved);
    }

    /// <summary>The staged panel's id from its fixture key (the first 8 hex digits).</summary>
    /// <param name="key">The key.</param>
    /// <returns>A stable id.</returns>
    public static Guid PanelId(string key) => Guid.Parse($"{key}-0000-0000-0000-000000000000");

    private static Hold Build(Guid id, JsonElement h, JsonElement size, JsonElement fp, Guid panel)
    {
        var hold = new Hold { Id = id, X = h[0].GetDouble(), Y = h[1].GetDouble(), Radius = h[5].GetDouble(), WallPanelId = panel };
        if (h[2].ValueKind == JsonValueKind.String)
        {
            (hold.FacetId, hold.PlaneAMm, hold.PlaneBMm) = (h[2].GetString(), h[3].GetDouble(), h[4].GetDouble());
        }

        (hold.WidthMm, hold.HeightMm) = (Number(size[0]), Number(size[1]));
        if (h[6].GetBoolean())
        {
            // The fixture keeps no outlines: a shaped hold gets a square of its radius, so the re-outline rule sees an
            // outlined hold without a warped outline and explains nothing (stricter than the run, never looser).
            var r = hold.Radius;
            hold.ShapePoints = [new() { Dx = -r, Dy = -r }, new() { Dx = r, Dy = -r }, new() { Dx = r, Dy = r }, new() { Dx = -r, Dy = r }];
        }

        if (fp.ValueKind == JsonValueKind.Array)
        {
            hold.FingerprintJson = new HoldFingerprint { Aspect = fp[0].GetDouble(), OrientationDeg = fp[1].GetDouble() }.ToJson();
        }

        return hold;
    }

    /// <summary>The run's protrusion, keyed to the hold as it is here (the fixture keeps no outline).</summary>
    private static void WithProtrusion(Hold hold, JsonElement prot)
    {
        if (prot.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        var source = Enum.Parse<HoldProtrusionSource>(prot[0].GetString()!);
        hold.ProtrusionMm = new HoldProtrusion(source, 0, prot[1].GetDouble(), 0, 0, 0, prot[2].GetDouble(), HoldFootprint.KeyOf(hold)).ToJson();
    }

    private static double? Number(JsonElement e) => e.ValueKind == JsonValueKind.Number ? e.GetDouble() : null;

    private static string Dir()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "test", "real-data", "attic");
            if (File.Exists(Path.Combine(candidate, "moves-endoct.json")))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException("test/real-data/attic/moves-endoct.json");
    }
}
