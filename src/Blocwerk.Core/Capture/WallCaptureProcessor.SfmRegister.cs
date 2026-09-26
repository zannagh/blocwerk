// <copyright file="WallCaptureProcessor.SfmRegister.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Entities;
using Blocwerk.Core.Geometry;
using Blocwerk.Core.Geometry.Registration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Capture;

/// <summary>The anchor photos a markerless capture sends along.</summary>
/// <param name="CaptureId">The capture they belong to (the active model's).</param>
/// <param name="ModelId">That capture's model: the reference the anchors are known in.</param>
/// <param name="ReferenceJson">Its JSON.</param>
/// <param name="Map">Anchor stem (<c>a00</c>, …) → reference camera image.</param>
/// <param name="Photos">The anchor photos.</param>
/// <param name="StemByIndex">Photo index → anchor stem.</param>
internal sealed record CaptureAnchors(
    Guid CaptureId,
    Guid ModelId,
    string ReferenceJson,
    IReadOnlyDictionary<string, string> Map,
    IReadOnlyList<WallCapturePhoto> Photos,
    IReadOnlyDictionary<int, string> StemByIndex);

/// <summary>
/// Markerless stage 2b: the anchors going in, and the gate coming out. A feature model is activated only when it is the
/// wall's first model, or when the solver fitted it into the active model's frame on the anchors (its gate: at least 6
/// anchors, rms ≤ 25 mm, each ≤ 60 mm, plane-ICP ≤ 80 mm / 3°) and at least one of its surfaces continues an active
/// facet. Otherwise it is stored inactive with a reason, exactly like a marker model that could not be registered.
/// </summary>
public sealed partial class WallCaptureProcessor
{
    /// <summary>
    /// The anchors: on a fresh reconstruction, the active model's capture; on a resumed one, what it was sent with
    /// (<see cref="WallCapture.AnchorCaptureId"/>; none when that is null). The selection is deterministic.
    /// </summary>
    private async Task<CaptureAnchors?> AnchorsAsync(WallCapture capture, CancellationToken ct)
    {
        await using var db = dbContextFactory.CreateDbContext();
        var anchorCaptureId = capture.SfmJobId is not null
            ? capture.AnchorCaptureId
            : await db.WallGeometryModels.AsNoTracking()
                .Where(m => m.WallId == capture.WallId && m.IsActive)
                .Join(db.WallCaptures, m => m.Id, c => c.GeometryModelId, (m, c) => (Guid?)c.Id)
                .FirstOrDefaultAsync(ct);
        var source = anchorCaptureId is null
            ? null
            : await db.WallCaptures.AsNoTracking()
                .Where(c => c.Id == anchorCaptureId && c.Id != capture.Id && c.GeometryModelId != null)
                .Join(db.WallGeometryModels, c => c.GeometryModelId, m => (Guid?)m.Id, (c, m) => new { CaptureId = c.Id, ModelId = m.Id, m.Json })
                .FirstOrDefaultAsync(ct);
        if (source is null)
        {
            return null;
        }

        var stored = (await db.WallCapturePhotos.AsNoTracking().Where(p => p.CaptureId == source.CaptureId).OrderBy(p => p.Index).ToListAsync(ct))
            .Where(p => files.ResolvePhysicalPath(p.StoredPath) is { } path && File.Exists(path))
            .ToDictionary(p => CaptureComputeDocuments.PhotoName(p.Index), StringComparer.Ordinal);
        var images = AnchorPhotoSelector.Select(source.Json, stored.Keys.ToHashSet(StringComparer.Ordinal));
        if (images.Count == 0)
        {
            logger.LogInformation("Capture {CaptureId}: no stored photo of capture {Anchor} can serve as an anchor", capture.Id, source.CaptureId);
            return null;
        }

        var map = CaptureSfmDocuments.AnchorMap(images);
        var photos = images.Select(i => stored[i]).ToList();
        var stems = map.ToDictionary(kv => stored[kv.Value].Index, kv => kv.Key);
        return new CaptureAnchors(source.CaptureId, source.ModelId, source.Json, map, photos, stems);
    }

    private async Task<FrameOutcome> RegisterFeaturesAsync(CaptureRun run, string solvedJson, CaptureAnchors? anchors, CancellationToken ct)
    {
        await using var db = dbContextFactory.CreateDbContext();
        var capture = run.Capture;
        var active = await db.WallGeometryModels.AsNoTracking()
            .Where(m => m.WallId == capture.WallId && m.IsActive)
            .Select(m => new { m.Id, m.Json })
            .FirstOrDefaultAsync(ct);
        if (active is null)
        {
            // The wall's first model: it defines the frame.
            return new FrameOutcome(solvedJson, true, null);
        }

        var refusal = FeatureRefusal(solvedJson, anchors, active.Id);
        if (refusal is null)
        {
            var result = WallFrameRegistrationWriter.RewriteFeatures(solvedJson, active.Json, active.Id);
            if (result.Claims.Count > 0)
            {
                logger.LogInformation(
                    "Capture {CaptureId}: feature model tied to model {ModelId} on anchors; facets {Claims}",
                    capture.Id, active.Id, string.Join(", ", result.Claims.Select(kv => $"{kv.Key}→{kv.Value}")));
                return new FrameOutcome(result.Json, true, null);
            }

            refusal = "None of the surfaces found in the new photos continues a surface of the current 3D model.";
        }

        logger.LogWarning("Capture {CaptureId}: feature model not tied to active model {ModelId}: {Reason}", capture.Id, active.Id, refusal);
        return new FrameOutcome(solvedJson, false, refusal);
    }

    /// <summary>Why an anchored registration is impossible, or null when the solver anchored the model to the active one.</summary>
    private static string? FeatureRefusal(string solvedJson, CaptureAnchors? anchors, Guid activeId)
    {
        if (anchors is null)
        {
            return "No photos of the current 3D model's capture are stored any more, so the new photos could not be tied to it.";
        }

        if (anchors.ModelId != activeId)
        {
            return "The active 3D model changed while this capture was running.";
        }

        if (WallGeometryDocument.Parse(solvedJson).World?.Anchored == true)
        {
            return null;
        }

        var reason = CaptureSfmDocuments.AnchorRefusal(solvedJson) ?? "the anchor photos did not register";
        return $"The new photos could not be tied to the current 3D model ({reason}).";
    }
}
