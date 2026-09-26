// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Capture.Corrections;

/// <summary>
/// A capture's model and its corrections (<see cref="WallGeometryModel.DerivedFromModelId"/>) form a family. The capture
/// row points at the family member that is live, so everything keyed on "the capture of the active model" (photo retention,
/// anchor photos, hold footprints, the follow-up chain) keeps working after a correction and after a revert.
/// </summary>
public static class ModelFamily
{
    /// <summary>The family's first model (the one a capture or an upload produced).</summary>
    /// <param name="parents">Model id → the model it was derived from.</param>
    /// <param name="modelId">Any member.</param>
    /// <returns>The root id.</returns>
    public static Guid Root(IReadOnlyDictionary<Guid, Guid?> parents, Guid modelId)
    {
        var current = modelId;
        for (var guard = 0; guard < 1000 && parents.GetValueOrDefault(current) is { } parent; guard++)
        {
            current = parent;
        }

        return current;
    }

    /// <summary>
    /// Points the finished capture of <paramref name="modelId"/>'s family at <paramref name="modelId"/> (a revert to the
    /// model a correction was derived from, or back to a correction) and clears its follow-up record so the chain runs
    /// again. Saves. Null when no capture was repointed.
    /// </summary>
    /// <param name="db">An admin context of the wall.</param>
    /// <param name="wallId">The wall.</param>
    /// <param name="modelId">The model just activated.</param>
    /// <returns>The repointed capture, or null.</returns>
    public static async Task<Guid?> RepointCaptureAsync(BlocwerkDbContext db, Guid wallId, Guid modelId)
    {
        var parents = await db.WallGeometryModels.Where(m => m.WallId == wallId)
            .Select(m => new { m.Id, m.DerivedFromModelId })
            .ToDictionaryAsync(m => m.Id, m => m.DerivedFromModelId);
        var root = Root(parents, modelId);
        var family = parents.Keys.Where(id => Root(parents, id) == root).ToList();
        if (family.Count < 2)
        {
            return null;
        }

        var capture = await db.WallCaptures
            .Where(c => c.WallId == wallId && c.GeometryModelId != null && family.Contains(c.GeometryModelId.Value))
            .OrderByDescending(c => c.CreatedAt)
            .FirstOrDefaultAsync();
        if (capture is null || capture.GeometryModelId == modelId || WallCaptureProcessor.IsRunnable(capture.Status))
        {
            return null;
        }

        capture.GeometryModelId = modelId;
        capture.FollowUpJson = null;
        capture.CoverageJson = null;
        await db.SaveChangesAsync();
        return capture.Id;
    }
}
