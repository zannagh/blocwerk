// <copyright file="CaptureDetectionStore.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Text.Json;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Geometry.Proposals;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Services;

/// <summary>
/// The persistent side of <see cref="CaptureDetectionCache"/>: one row per photo and detector, written as soon as the
/// photo is detected. A hold search that a restart or a deploy cut off therefore resumes at the first photo it had not
/// looked at, instead of detecting all of them again.
/// </summary>
internal static class CaptureDetectionStore
{
    /// <summary>How long a photo's detections are kept (a capture's photos never change; the detector might).</summary>
    public static readonly TimeSpan Retention = TimeSpan.FromDays(30);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>The stored detections of the photos by the detector, by stored path.</summary>
    public static async Task<Dictionary<string, List<CaptureDetection>>> LoadAsync(
        BlocwerkDbContext db, IReadOnlyCollection<string> paths, string detector, CancellationToken ct)
    {
        var wanted = paths.ToList();
        var rows = await db.CaptureDetectionRecords.AsNoTracking()
            .Where(r => r.Detector == detector && wanted.Contains(r.StoredPath))
            .Select(r => new { r.StoredPath, r.DetectionsJson })
            .ToListAsync(ct);
        var result = new Dictionary<string, List<CaptureDetection>>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            try
            {
                result[row.StoredPath] = JsonSerializer.Deserialize<List<CaptureDetection>>(row.DetectionsJson, Json) ?? [];
            }
            catch (JsonException)
            {
                // A row from another shape of the record: the photo is simply detected again.
            }
        }

        return result;
    }

    /// <summary>Saves one photo's detections (the checkpoint), replacing an older row of the same photo and detector.</summary>
    public static async Task SaveAsync(
        BlocwerkDbContext db, string path, string detector, IReadOnlyList<CaptureDetection> detections, CancellationToken ct)
    {
        var row = await db.CaptureDetectionRecords.FirstOrDefaultAsync(r => r.StoredPath == path && r.Detector == detector, ct);
        if (row is null)
        {
            row = new CaptureDetectionRecord { StoredPath = path, Detector = detector };
            db.CaptureDetectionRecords.Add(row);
        }

        row.DetectionsJson = JsonSerializer.Serialize(detections, Json);
        row.CreatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();
    }

    /// <summary>Drops rows older than <see cref="Retention"/>.</summary>
    public static async Task PruneAsync(BlocwerkDbContext db, CancellationToken ct)
    {
        var cutoff = DateTimeOffset.UtcNow - Retention;
        var old = (await db.CaptureDetectionRecords.AsNoTracking().Select(r => new { r.StoredPath, r.Detector, r.CreatedAt }).ToListAsync(ct))
            .Where(r => r.CreatedAt < cutoff)
            .ToList();
        foreach (var r in old)
        {
            await db.CaptureDetectionRecords.Where(x => x.StoredPath == r.StoredPath && x.Detector == r.Detector).ExecuteDeleteAsync(ct);
        }
    }
}
