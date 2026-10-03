// <copyright file="CapturePlanLayoutCheck.Checks.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Globalization;

namespace Blocwerk.Core.Capture;

/// <summary>The triangle-orientation and the size/depth checks.</summary>
public static partial class CapturePlanLayoutCheck
{
    /// <summary>
    /// A triangle is judged only when it is this "fat" (area over an equilateral's of the same perimeter) in the
    /// plan, so a marker placed a little off its planned spot cannot flip it.
    /// </summary>
    internal const double MinPlanFatness = 0.15;

    /// <summary>... and at least this fat in the photo (a grazing view squeezes every triangle).</summary>
    internal const double MinImageFatness = 0.05;

    /// <summary>
    /// Depth from size: two markers' implied depths may differ by at most this many times their plan distance
    /// (plus <see cref="DepthSlackMm"/>). Real pairs on The Attic reached 1.9 (placement, lens, oblique views).
    /// </summary>
    internal const double MaxDepthPerPlanDistance = 3.0;

    /// <summary>Slack of the depth check, mm.</summary>
    internal const double DepthSlackMm = 300;

    /// <summary>Ignores the marker that flips two or more well-shaped triangles of its segment (repeatedly).</summary>
    private static List<IgnoredCaptureMarker> CheckOrientation(List<PlanLayoutView> members)
    {
        var ignored = new List<IgnoredCaptureMarker>();
        while (members.Count >= 3)
        {
            var flips = members.ToDictionary(m => m, _ => 0);
            var partners = members.ToDictionary(m => m, _ => new HashSet<PlanLayoutView>());
            foreach (var (a, b, c) in Triangles(members).Where(t => Mirrored(t.A, t.B, t.C)))
            {
                foreach (var (v, p, q) in new[] { (a, b, c), (b, a, c), (c, a, b) })
                {
                    flips[v]++;
                    partners[v].UnionWith([p, q]);
                }
            }

            var worst = UniqueWorst(flips, 2);
            if (worst is null)
            {
                return ignored;
            }

            members.Remove(worst);
            ignored.Add(new(worst.Marker, $"mirrored against {Ids(partners[worst])} compared with the plan"));
        }

        return ignored;
    }

    private static IEnumerable<(PlanLayoutView A, PlanLayoutView B, PlanLayoutView C)> Triangles(List<PlanLayoutView> members)
    {
        for (var i = 0; i < members.Count; i++)
        {
            for (var j = i + 1; j < members.Count; j++)
            {
                for (var k = j + 1; k < members.Count; k++)
                {
                    yield return (members[i], members[j], members[k]);
                }
            }
        }
    }

    private static bool Mirrored(PlanLayoutView a, PlanLayoutView b, PlanLayoutView c)
    {
        var plan = Cross(a.PlanX, a.PlanY, b.PlanX, b.PlanY, c.PlanX, c.PlanY);
        var image = Cross(a.X, a.Up, b.X, b.Up, c.X, c.Up);
        return Fatness(plan, a.PlanDistance(b) + b.PlanDistance(c) + c.PlanDistance(a)) >= MinPlanFatness
               && Fatness(image, ImageDistance(a, b) + ImageDistance(b, c) + ImageDistance(c, a)) >= MinImageFatness
               && Math.Sign(plan) != Math.Sign(image);
    }

    /// <summary>Ignores the marker whose size-implied depth contradicts two or more others of its segment (repeatedly).</summary>
    private static List<IgnoredCaptureMarker> CheckDepth(List<PlanLayoutView> members, double focalPx)
    {
        var ignored = new List<IgnoredCaptureMarker>();
        while (members.Count >= 3)
        {
            var depth = members.ToDictionary(m => m, m => focalPx * m.SizeMm / Math.Max(1, m.MaxEdgePx));
            var partners = members.ToDictionary(m => m, _ => new HashSet<PlanLayoutView>());
            foreach (var a in members)
            {
                foreach (var b in members.Where(b => !ReferenceEquals(a, b)))
                {
                    if (Math.Abs(depth[a] - depth[b]) > (MaxDepthPerPlanDistance * a.PlanDistance(b)) + DepthSlackMm)
                    {
                        partners[a].Add(b);
                    }
                }
            }

            var worst = UniqueWorst(partners.ToDictionary(p => p.Key, p => p.Value.Count), 2);
            if (worst is null)
            {
                return ignored;
            }

            members.Remove(worst);
            var mm = depth[worst].ToString("0", CultureInfo.InvariantCulture);
            ignored.Add(new(worst.Marker, $"its size puts it {mm} mm from the camera, which the plan rules out next to {Ids(partners[worst])}"));
        }

        return ignored;
    }

    private static double Cross(double ax, double ay, double bx, double by, double cx, double cy) =>
        ((bx - ax) * (cy - ay)) - ((by - ay) * (cx - ax));

    /// <summary>Area over the area of an equilateral triangle with the same perimeter (1 = equilateral, 0 = flat).</summary>
    private static double Fatness(double cross, double perimeter) =>
        perimeter <= 0 ? 0 : Math.Abs(cross) / 2 / (perimeter * perimeter / (12 * Math.Sqrt(3)));

    private static double ImageDistance(PlanLayoutView a, PlanLayoutView b) => Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Up - b.Up, 2));
}
