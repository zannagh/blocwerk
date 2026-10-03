namespace Blocwerk.Core.Detection.Outlines;

/// <summary>
/// Plain automatic circles that overlap EACH OTHER (the detector often reports one hold twice or two touching
/// holds as overlapping discs) share the blame: both shrink in proportion to their size so they just clear, instead
/// of the one that happens to be processed first keeping its full size and the other having no room at all.
/// </summary>
internal static class ShapeCirclePrepass
{
    /// <summary>The radius each circle starts the sequential fit with (only circles that changed are listed).</summary>
    /// <param name="holds">Every hold of the panel (only unlocked, outline-less ones take part).</param>
    /// <param name="floorOf">The smallest radius allowed for a circle of the given original radius.</param>
    /// <returns>The shrunk radii by hold id.</returns>
    public static Dictionary<Guid, double> Shrink(IReadOnlyList<HoldShapeInput> holds, Func<double, double> floorOf)
    {
        var circles = holds.Where(h => !h.Locked && h.Shape is not { Count: >= 3 }).OrderBy(h => h.X).ToList();
        var radius = circles.ToDictionary(h => h.Id, h => h.Radius);
        if (circles.Count < 2)
        {
            return [];
        }

        double reach = (circles.Max(h => h.Radius) * 2) + HoldShapeOverlapResolver.Tolerance;
        var pairs = new List<(HoldShapeInput A, HoldShapeInput B)>();
        for (int i = 0; i < circles.Count; i++)
        {
            for (int j = i + 1; j < circles.Count && circles[j].X - circles[i].X <= reach; j++)
            {
                pairs.Add(circles[i].Id.CompareTo(circles[j].Id) < 0 ? (circles[i], circles[j]) : (circles[j], circles[i]));
            }
        }

        foreach (var (a, b) in pairs.OrderBy(p => p.A.Id).ThenBy(p => p.B.Id))
        {
            Share(a, b, radius, floorOf);
        }

        var original = circles.ToDictionary(h => h.Id, h => h.Radius);
        return radius.Where(kv => kv.Value < original[kv.Key] - 1e-12).ToDictionary(kv => kv.Key, kv => kv.Value);
    }

    private static void Share(HoldShapeInput a, HoldShapeInput b, Dictionary<Guid, double> radius, Func<double, double> floorOf)
    {
        double d = Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));
        double ra = radius[a.Id];
        double rb = radius[b.Id];
        double room = d - HoldShapeOverlapResolver.Tolerance;
        if (ra + rb <= room)
        {
            return;
        }

        double na = Math.Clamp(room * ra / (ra + rb), Math.Min(floorOf(a.Radius), ra), ra);
        double nb = Math.Clamp(room - na, Math.Min(floorOf(b.Radius), rb), rb);
        if (na + nb > room + 1e-9)
        {
            // Even both at their floors would still touch: leave them (the sequential fit reports what is left).
            // Shrinking anyway would also ratchet, because the floor is a share of the CURRENT radius.
            return;
        }

        radius[a.Id] = Math.Floor(na * 1e6) / 1e6;
        radius[b.Id] = Math.Floor(nb * 1e6) / 1e6;
    }
}
