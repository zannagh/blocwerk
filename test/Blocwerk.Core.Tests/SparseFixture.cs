// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using Blocwerk.Core.Geometry.Corrections;

namespace Blocwerk.Core.Tests;

/// <summary>
/// Synthetic sparse models: a splat-prepare <c>sparse.zip</c> (COLMAP binary images/points + stems.json) whose frame is
/// the wall world moved by <see cref="WorldToColmap"/>, and a model whose photo cameras p01…p06 stand 3 m in front of the
/// vertical test facet (a along +x, b up, normal −y).
/// </summary>
internal static class SparseFixture
{
    /// <summary>Wall world → the reconstruction's own frame: a turn, a shrink to "COLMAP units" and a shift.</summary>
    public static readonly GeometrySimilarity WorldToColmap =
        GeometrySimilarity.RotationBetween([0, 0, 1], [0.3, -0.2, 1], [0, 0, 0]) with { Scale = 1 / 480.0, Translation = [0.7, -1.1, 2.4] };

    /// <summary>The photo camera centres in the wall world (mm).</summary>
    public static readonly IReadOnlyDictionary<string, double[]> Photos = Enumerable.Range(0, 6).ToDictionary(
        i => $"p{i + 1:00}", i => new[] { 500.0 + (1000 * (i % 3)), -3000, 700 + (600 * (i / 3)) }, StringComparer.Ordinal);

    /// <summary>A sparse.zip of the world points (plus a video frame and an anchor image, and a few two-view points).</summary>
    public static byte[] Zip(IEnumerable<(float X, float Y, float Z)> worldPoints, IReadOnlyDictionary<string, double[]>? photos = null)
    {
        photos ??= Photos;
        var images = photos.Select(kv => (Name: kv.Key + ".jpg", Stem: kv.Key, Role: "photo", Centre: WorldToColmap.Apply(kv.Value))).ToList();
        images.Add(("vf_0001.jpg", "vf_0001", "frame", WorldToColmap.Apply([1500, -2500, 1000])));
        images.Add(("a00.jpg", "a00", "anchor", WorldToColmap.Apply([2500, -2600, 900])));
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            Add(zip, "cameras.bin", BitConverter.GetBytes(0UL));
            Add(zip, "images.bin", ImagesBin(images));
            Add(zip, "points3D.bin", PointsBin(worldPoints.Select(p => WorldToColmap.Apply([p.X, p.Y, p.Z])).ToList()));
            var stems = new JsonObject
            {
                ["version"] = 1,
                ["images"] = new JsonObject(images.Select(i => KeyValuePair.Create(
                    i.Name, (JsonNode?)new JsonObject { ["stem"] = i.Stem, ["role"] = i.Role, ["width"] = 2000, ["height"] = 1500 }))),
            };
            Add(zip, "stems.json", Encoding.UTF8.GetBytes(stems.ToJsonString()));
        }

        return output.ToArray();
    }

    /// <summary>A model with the vertical facet "0" (3000 × 2000 mm) and the photo cameras.</summary>
    public static string ModelJson(double width = 3000, double height = 2000) => new JsonObject
    {
        ["version"] = 1, ["units"] = "mm", ["markerSizeMm"] = 125.0,
        ["world"] = new JsonObject { ["up"] = new JsonArray(0.0, 0.0, 1.0), ["gravityKnown"] = true, ["frameSource"] = "features" },
        ["segments"] = new JsonArray(new JsonObject
        {
            ["index"] = 0, ["name"] = "Main surface",
            ["facets"] = new JsonArray(new JsonObject
            {
                ["id"] = "0", ["origin"] = new JsonArray(0.0, 0.0, 0.0), ["u"] = new JsonArray(1.0, 0.0, 0.0),
                ["v"] = new JsonArray(0.0, 0.0, 1.0), ["normal"] = new JsonArray(0.0, -1.0, 0.0),
                ["extentMm"] = new JsonObject { ["aMin"] = 0.0, ["aMax"] = width, ["bMin"] = 0.0, ["bMax"] = height },
            }),
        }),
        ["markers"] = new JsonArray(),
        ["cameras"] = new JsonArray(Photos.Select(kv => (JsonNode?)Camera(kv.Key, kv.Value)).ToArray()),
    }.ToJsonString();

    /// <summary>Wall points every <paramref name="stepMm"/> with ±<paramref name="noiseMm"/>, lifted by <paramref name="lift"/>(a, b).</summary>
    public static List<(float X, float Y, float Z)> Surface(Func<double, double, double> lift, double stepMm = 15, double noiseMm = 12, int seed = 5)
    {
        var rng = new Random(seed);
        var points = new List<(float, float, float)>();
        for (var a = 5.0; a < 3000; a += stepMm)
        {
            for (var b = 5.0; b < 2000; b += stepMm)
            {
                // Jittered in the plane like real sparse points, noisy off it.
                var pa = a + ((rng.NextDouble() - 0.5) * stepMm);
                var pb = b + ((rng.NextDouble() - 0.5) * stepMm);
                var h = lift(pa, pb) + ((rng.NextDouble() - 0.5) * 2 * noiseMm);
                points.Add(((float)pa, (float)(-h), (float)pb));
            }
        }

        return points;
    }

    private static JsonObject Camera(string image, double[] c) => new()
    {
        ["image"] = image, ["width"] = 2000, ["height"] = 1500,
        ["K"] = new JsonArray(1000.0, 0.0, 1000.0, 0.0, 1000.0, 750.0, 0.0, 0.0, 1.0),
        ["dist"] = new JsonArray(0.0, 0.0, 0.0, 0.0, 0.0),
        ["R"] = new JsonArray(1.0, 0.0, 0.0, 0.0, 0.0, -1.0, 0.0, 1.0, 0.0),
        ["t"] = new JsonArray(-c[0], c[2], -c[1]),
    };

    private static void Add(ZipArchive zip, string name, byte[] bytes)
    {
        using var stream = zip.CreateEntry(name).Open();
        stream.Write(bytes);
    }

    /// <summary>images.bin with identity rotations (the centre is then −t) and two dummy observations each.</summary>
    private static byte[] ImagesBin(List<(string Name, string Stem, string Role, double[] Centre)> images)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write((ulong)images.Count);
        for (var i = 0; i < images.Count; i++)
        {
            var (name, _, _, c) = images[i];
            w.Write(i + 1);
            w.Write(1.0);
            w.Write(0.0);
            w.Write(0.0);
            w.Write(0.0);
            w.Write(-c[0]);
            w.Write(-c[1]);
            w.Write(-c[2]);
            w.Write(1);
            w.Write(Encoding.UTF8.GetBytes(name));
            w.Write((byte)0);
            w.Write(2UL);
            for (var k = 0; k < 2; k++)
            {
                w.Write(10.0 * k);
                w.Write(20.0);
                w.Write(-1L);
            }
        }

        w.Flush();
        return ms.ToArray();
    }

    /// <summary>points3D.bin: every point seen by 3 images at 0.5 px, plus three two-view points (kept) and one single-view one (dropped).</summary>
    private static byte[] PointsBin(List<double[]> points)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write((ulong)points.Count + 1);
        for (var i = 0; i <= points.Count; i++)
        {
            var p = i < points.Count ? points[i] : [0, 0, 0];
            var track = i < points.Count ? (i < 3 ? 2 : 3) : 1;
            w.Write((ulong)i + 1);
            w.Write(p[0]);
            w.Write(p[1]);
            w.Write(p[2]);
            w.Write(new byte[] { 200, 100, 50 });
            w.Write(0.5);
            w.Write((ulong)track);
            for (var k = 0; k < track; k++)
            {
                w.Write(k + 1);
                w.Write(0);
            }
        }

        w.Flush();
        return ms.ToArray();
    }
}
