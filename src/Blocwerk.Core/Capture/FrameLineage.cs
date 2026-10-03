// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Text.Json;
using System.Text.Json.Nodes;
using Blocwerk.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Capture;

/// <summary>
/// Which models share a wall frame. A model is tied to its predecessor by a registration (<c>quality.registration</c>'s
/// <c>referenceModelId</c>) or derived from it by a correction (<see cref="Entities.WallGeometryModel.DerivedFromModelId"/>).
/// A model activated WITHOUT being tied to the model it replaced starts a new frame and says so in <c>quality.frameReset</c>
/// (the upgrade of a wall without markers to markers: the marker model defines the frame, the old model stays in the
/// history). Positions known in a model before the reset (hold placements) are in another frame: they are never carried
/// through world space onto the new one, they are derived again.
/// </summary>
public static class FrameLineage
{
    private const string ResetKey = "frameReset";

    /// <summary>The document marked as starting a new frame, replacing <paramref name="previousModelId"/>.</summary>
    /// <param name="json">The document.</param>
    /// <param name="previousModelId">The model it replaces without being tied to it.</param>
    /// <param name="reason">Why it could not be tied (for the history).</param>
    /// <returns>The stamped document.</returns>
    public static string StampReset(string json, Guid previousModelId, string reason)
    {
        var root = JsonNode.Parse(json)!.AsObject();
        if (root["quality"] is not JsonObject quality)
        {
            quality = [];
            root["quality"] = quality;
        }

        quality[ResetKey] = new JsonObject { ["previousModelId"] = previousModelId.ToString(), ["reason"] = reason };
        return root.ToJsonString();
    }

    /// <summary>Whether a document starts a new frame.</summary>
    /// <param name="json">The document.</param>
    /// <returns>True when stamped by <see cref="StampReset"/>.</returns>
    public static bool IsReset(string json) => Parse(json)?["quality"]?[ResetKey] is JsonObject;

    /// <summary>
    /// The models in <paramref name="activeId"/>'s frame since the last frame reset in its lineage (the reset model and every
    /// model tied to it, directly or through others); null when its lineage has no reset (every model shares the frame).
    /// </summary>
    /// <param name="db">A context.</param>
    /// <param name="wallId">The wall.</param>
    /// <param name="activeId">The active model.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The model ids, or null.</returns>
    public static async Task<IReadOnlySet<Guid>?> SameFrameAsync(BlocwerkDbContext db, Guid wallId, Guid activeId, CancellationToken ct)
    {
        var rows = await db.WallGeometryModels.AsNoTracking()
            .Where(m => m.WallId == wallId)
            .Select(m => new { m.Id, m.DerivedFromModelId, m.Json })
            .ToListAsync(ct);
        var parent = new Dictionary<Guid, Guid?>();
        var resets = new HashSet<Guid>();
        foreach (var row in rows)
        {
            var root = Parse(row.Json);
            var reference = root?["quality"]?["registration"]?["referenceModelId"] is JsonValue v && v.TryGetValue<string>(out var text)
                && Guid.TryParse(text, out var id) ? id : (Guid?)null;
            parent[row.Id] = row.DerivedFromModelId ?? reference;
            if (root?["quality"]?[ResetKey] is JsonObject)
            {
                resets.Add(row.Id);
            }
        }

        var reset = Ancestors(activeId, parent).FirstOrDefault(resets.Contains);
        if (reset == Guid.Empty)
        {
            return null;
        }

        return parent.Keys.Where(m => Ancestors(m, parent).Contains(reset)).ToHashSet();
    }

    /// <summary>The model itself, then what it was tied to or derived from, and so on (cycles cut).</summary>
    private static List<Guid> Ancestors(Guid model, Dictionary<Guid, Guid?> parent)
    {
        var chain = new List<Guid>();
        for (Guid? current = model; current is { } c && !chain.Contains(c) && chain.Count < 1000; current = parent.GetValueOrDefault(c))
        {
            chain.Add(c);
        }

        return chain;
    }

    private static JsonNode? Parse(string json)
    {
        try
        {
            return JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
