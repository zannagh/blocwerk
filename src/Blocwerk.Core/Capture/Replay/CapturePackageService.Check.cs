// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Text.Json;
using System.Text.Json.Nodes;
using Blocwerk.Core.Data;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Capture.Replay;

/// <summary>
/// The dry run against this server: the same capture already here (idempotent: nothing to do), ids taken by other rows,
/// the wall, its owner and plan revision, a newer active model, the schema, the splat worker, the files and the disk.
/// </summary>
public sealed partial class CapturePackageService
{
    private async Task<CaptureImportReport> CheckAsync(CapturePackageManifest m, CancellationToken ct)
    {
        var blockers = new List<string>();
        var warnings = m.Warnings.Select(w => $"Source: {w}").ToList();
        if (m.Rows.GpuJob.GeometryModelId != m.Rows.Model.Id)
        {
            warnings.Add($"The capture's model {m.Rows.Model.Id} was solved again after training; the trained view (trained for model {m.Rows.GpuJob.GeometryModelId}, same frame) is installed for it.");
        }

        bool already;
        await using (var db = dbContextFactory.CreateDbContext())
        {
            already = await db.WallCaptures.AnyAsync(
                c => c.Id == m.CaptureId && c.WallId == m.WallId && c.GeometryModelId == m.Rows.Model.Id, ct);
            if (!already)
            {
                await CollisionsAsync(db, m, blockers, ct);
                await TargetProblemsAsync(db, m, blockers, warnings, ct);
                await ModelProblemsAsync(db, m, blockers, warnings, ct);
                SchemaProblems(db, m, blockers, warnings);
            }
        }

        if (!already)
        {
            await WorkerProblemsAsync(blockers, warnings, ct);
        }

        var states = await FileStatesAsync(m, ct);
        blockers.AddRange(states.Where(f => f.State == CaptureImportFileState.Conflict)
            .Select(f => $"A different file named {f.Name} is already in the capture store here."));
        var missing = states.Where(f => f.State == CaptureImportFileState.Missing).Sum(f => f.Bytes);
        var free = disk.FreeBytes(staging.StoreFolder());
        var needed = missing + (m.Rows.GpuJob.ResultBytes ?? 0);
        if (!already && free is { } f && f < needed)
        {
            blockers.Add($"Only {f / (1024 * 1024)} MB are free here; the upload and the finished view need about {needed / (1024 * 1024)} MB.");
        }

        return new CaptureImportReport(m.CaptureId, m.CaptureId, m.WallId, already, false, blockers, warnings, states, missing, free);
    }

    private static async Task CollisionsAsync(BlocwerkDbContext db, CapturePackageManifest m, List<string> blockers, CancellationToken ct)
    {
        var photoIds = m.Rows.Photos.Select(p => p.Id).ToList();
        var textureIds = m.Rows.Textures.Select(t => t.Id).ToList();
        var taken = new List<string>();
        if (await db.WallCaptures.AnyAsync(c => c.Id == m.CaptureId, ct))
        {
            taken.Add($"capture {m.CaptureId}");
        }

        if (await db.WallGeometryModels.AnyAsync(x => x.Id == m.Rows.Model.Id, ct))
        {
            taken.Add($"model {m.Rows.Model.Id}");
        }

        if (await db.GpuJobs.AnyAsync(j => j.Id == m.Rows.GpuJob.Id, ct))
        {
            taken.Add($"GPU job {m.Rows.GpuJob.Id}");
        }

        taken.AddRange((await db.WallCapturePhotos.Where(p => photoIds.Contains(p.Id)).Select(p => p.Id).ToListAsync(ct)).Select(id => $"photo {id}"));
        taken.AddRange((await db.WallGeometryTextures.Where(t => textureIds.Contains(t.Id)).Select(t => t.Id).ToListAsync(ct)).Select(id => $"texture {id}"));
        if (taken.Count > 0)
        {
            blockers.Add($"Ids already present here (another or a partial import?): {string.Join(", ", taken.Take(8))}{(taken.Count > 8 ? ", …" : string.Empty)}.");
        }
    }

    private static async Task TargetProblemsAsync(
        BlocwerkDbContext db, CapturePackageManifest m, List<string> blockers, List<string> warnings, CancellationToken ct)
    {
        var wall = await db.Walls.IgnoreQueryFilters().AsNoTracking().Where(w => w.Id == m.WallId)
            .Select(w => new { w.GlyphsEnabled }).FirstOrDefaultAsync(ct);
        if (wall is null)
        {
            blockers.Add($"Wall {m.WallId} does not exist here.");
            return;
        }

        var owner = await db.Users.IgnoreQueryFilters().AnyAsync(u => u.Id == m.OwnerUserId && u.DeletedAt == null, ct);
        if (!owner)
        {
            blockers.Add($"The capture's owner {m.OwnerUserId} has no account here.");
        }
        else if (!await WallAdminGuard.IsWallAdminAsync(db, m.WallId, m.OwnerUserId, ct))
        {
            blockers.Add($"The capture's owner {m.OwnerUserId} is not an admin of the wall here (the pipeline runs as them).");
        }

        if (!wall.GlyphsEnabled && m.Rows.Model.FrameSource == Entities.WallGeometryFrameSource.Markers)
        {
            warnings.Add("Printed markers are switched off for this wall here; switch them on (marker size) as on the source.");
        }

        await PlanProblemsAsync(db, m, blockers, warnings, ct);
    }

    private static async Task ModelProblemsAsync(
        BlocwerkDbContext db, CapturePackageManifest m, List<string> blockers, List<string> warnings, CancellationToken ct)
    {
        var model = m.Rows.Model;
        var active = await db.WallGeometryModels.AsNoTracking().Where(x => x.WallId == m.WallId && x.IsActive)
            .Select(x => new { x.Id, x.CreatedAt }).FirstOrDefaultAsync(ct);
        if (active is not null && active.CreatedAt >= model.CreatedAt)
        {
            blockers.Add($"This wall already has a newer active model here ({active.Id}, {active.CreatedAt:u}); the package's is from {model.CreatedAt:u}.");
            return;
        }

        if (active is not null)
        {
            warnings.Add($"The package's model replaces this wall's active model {active.Id} here (as a new frame: hold positions are derived again).");
        }

        if (m.ReferenceModelId is { } reference && !await db.WallGeometryModels.AnyAsync(x => x.Id == reference, ct))
        {
            warnings.Add($"The model was registered to model {reference}, which is not here; it becomes this wall's frame as it is (the registration is kept as history only).");
        }
    }

    private static void SchemaProblems(BlocwerkDbContext db, CapturePackageManifest m, List<string> blockers, List<string> warnings)
    {
        var here = NewestMigration(db);
        if (m.SourceMigration is null || here is null || m.SourceMigration == here)
        {
            return;
        }

        if (string.CompareOrdinal(m.SourceMigration, here) > 0)
        {
            blockers.Add($"The source's schema ({m.SourceMigration}) is newer than this server's ({here}): deploy the same build here first.");
        }
        else
        {
            warnings.Add($"The source's schema ({m.SourceMigration}) is older than this server's ({here}); columns it does not know keep their defaults.");
        }
    }

    private static bool SameJson(string a, string b)
    {
        try
        {
            return JsonNode.DeepEquals(JsonNode.Parse(a), JsonNode.Parse(b));
        }
        catch (JsonException)
        {
            return string.Equals(a, b, StringComparison.Ordinal);
        }
    }
}
