using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Blocwerk.Core.Data;
using Blocwerk.Core.Detection.Outlines;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Geometry.View3D;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Services;

/// <summary>Planning: load each live photo's holds once, version them, and re-plan only when they changed.</summary>
public sealed partial class HoldShapeCleanupService
{
    // The last plan per wall. A preview stores it and the apply that follows reuses it when the holds still have
    // the same version, so "apply" writes exactly what was previewed and the geometry is not computed twice.
    private static readonly ConcurrentDictionary<Guid, HoldShapeCleanupPlan> PlanCache = new();

    /// <summary>Loads the live holds (tracked) and returns them with their plan, cached by version.</summary>
    private async Task<(Dictionary<Guid, Hold> Holds, HoldShapeCleanupPlan Plan)> PlanAsync(
        BlocwerkDbContext db, Guid wallId, IProgress<HoldShapeCleanupProgress>? progress, CancellationToken ct)
    {
        var photos = await HoldOutlineUpgradeService.LoadLivePhotosAsync(db, wallId, ct);
        var panels = new List<(List<Hold> Holds, double Aspect)>();
        foreach (var photo in photos)
        {
            var holds = await HoldOutlineUpgradeService.LiveHolds(db, wallId, photo).ToListAsync(ct);
            panels.Add((holds, await PhotoAspectAsync(db, wallId, photo, ct)));
        }

        var all = panels.SelectMany(p => p.Holds).ToDictionary(h => h.Id);
        var version = VersionOf(panels);
        if (PlanCache.TryGetValue(wallId, out var cached) && cached.Version == version)
        {
            progress?.Report(new HoldShapeCleanupProgress(photos.Count, photos.Count));
            return (all, cached);
        }

        var plan = new HoldShapeCleanupPlan(version, photos.Count);
        for (int i = 0; i < panels.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            plan.Add(panels[i].Holds, HoldShapeCleanup.PlanDetailed(panels[i].Holds, panels[i].Aspect));
            progress?.Report(new HoldShapeCleanupProgress(i + 1, photos.Count));
        }

        PlanCache[wallId] = plan;
        return (all, plan);
    }

    /// <summary>A hash of everything the plan depends on, so a changed hold (or photo size) invalidates it.</summary>
    private static string VersionOf(List<(List<Hold> Holds, double Aspect)> panels)
    {
        var text = new StringBuilder();
        foreach (var (holds, aspect) in panels)
        {
            text.Append(CultureInfo.InvariantCulture, $"#{aspect:R}");
            foreach (var h in holds.OrderBy(h => h.Id))
            {
                text.Append(CultureInfo.InvariantCulture, $"|{h.Id:N},{h.X:R},{h.Y:R},{h.Radius:R},{h.IsAutoDetected},{h.IsVirtual},{h.OutlineSource}");
                foreach (var p in h.ShapePoints ?? [])
                {
                    text.Append(CultureInfo.InvariantCulture, $";{p.Dx:R},{p.Dy:R}");
                }
            }
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())), 0, 12);
    }

    /// <summary>Width / height of the photo the holds sit on (header read only); 1 when it cannot be read.</summary>
    private static async Task<double> PhotoAspectAsync(BlocwerkDbContext db, Guid wallId, OutlineUpgradePhoto photo, CancellationToken ct)
    {
        var key = new Wall3DPhotoKey(photo.PanelId, photo.Generation);
        var infos = await PanelPhotoInfoLoader.LoadAsync(db, wallId, [key], ct);
        return infos.TryGetValue(key, out var info) && info.Height > 0 ? (double)info.Width / info.Height : 1;
    }
}
