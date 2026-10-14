// <copyright file="RedetectHistory.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Text.Json;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.LookOver;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Redetect;

/// <summary>
/// What people already said about one panel photo, which a re-detect has to respect: the spots where there is no
/// hold (a discarded detection of the look-over that was answered "Not a hold" or is still waiting there, and holds a
/// person deleted from this photo), and the holds a person moved or resized by hand. Read from the look-over findings
/// and the change journal; reads only.
/// </summary>
internal static class RedetectHistory
{
    private const string DeleteLabel = "hold-delete";
    private const string EditLabel = "hold-edit";

    /// <summary>The spots of the panel's current photo with an earlier answer: "Not a hold", set aside, or a deleted hold.</summary>
    /// <param name="db">The context.</param>
    /// <param name="panel">The panel.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The spots, as holds that are never stored, with what was said there.</returns>
    public static async Task<List<RedetectTurnedDown>> TurnedDownAsync(BlocwerkDbContext db, WallPanel panel, CancellationToken ct)
    {
        var findings = await db.WallLookOverFindings.AsNoTracking()
            .Where(f => f.WallId == panel.WallId && f.PanelId == panel.Id && f.Generation == panel.Generation
                        && f.Kind == LookOverFindingKind.DiscardedDetection && f.Answer != LookOverAnswer.AddHold)
            .ToListAsync(ct);
        var spots = findings.Select(f => new RedetectTurnedDown(
            DiscardedDetections.AsHold(f),
            f.Answer is null ? new RedetectEarlier(RedetectEarlierKind.SetAside, f.CreatedAt) : new RedetectEarlier(RedetectEarlierKind.NotAHold, f.AnsweredAt))).ToList();
        foreach (var before in await EntriesAsync(db, panel, DeleteLabel, ChangeJournalOp.Delete, ct))
        {
            if (Read(before.Before) is { } row && row.PanelId == panel.Id && row.Generation == panel.Generation)
            {
                spots.Add(new RedetectTurnedDown(
                    new Hold { WallId = panel.WallId, WallPanelId = panel.Id, Generation = panel.Generation, X = row.X, Y = row.Y, Radius = row.Radius },
                    new RedetectEarlier(RedetectEarlierKind.Deleted, before.When)));
            }
        }

        return spots;
    }

    /// <summary>The holds of the panel a person moved or resized in the editor.</summary>
    /// <param name="db">The context.</param>
    /// <param name="panel">The panel.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>Their ids.</returns>
    public static async Task<HashSet<Guid>> HandEditedAsync(BlocwerkDbContext db, WallPanel panel, CancellationToken ct)
    {
        var edited = new HashSet<Guid>();
        foreach (var entry in await EntriesAsync(db, panel, EditLabel, ChangeJournalOp.Update, ct))
        {
            // The row after the edit may only carry what changed: a field it lacks kept its value.
            if (Read(entry.Before) is { } before && before.PanelId == panel.Id && Moved(before, entry.After) && Text(entry.Key, nameof(Hold.Id)) is { } id
                && Guid.TryParse(id, out var holdId))
            {
                edited.Add(holdId);
            }
        }

        return edited;
    }

    private static bool Moved((Guid? PanelId, int Generation, double X, double Y, double Radius) before, string? after) =>
        Differs(before.X, Number(after, nameof(Hold.X))) || Differs(before.Y, Number(after, nameof(Hold.Y))) || Differs(before.Radius, Number(after, nameof(Hold.Radius)));

    private static bool Differs(double before, double? after) => after is { } value && Math.Abs(before - value) > Hold.GeometryEditTolerance;

    private static double? Number(string? json, string name) => Value(json, name, e => e.ValueKind == JsonValueKind.Number ? e.GetDouble() : (double?)null);

    private static string? Text(string? json, string name) => Value(json, name, e => e.ValueKind == JsonValueKind.String ? e.GetString() : null);

    private static T? Value<T>(string? json, string name, Func<JsonElement, T?> read)
    {
        if (string.IsNullOrEmpty(json))
        {
            return default;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty(name, out var element) ? read(element) : default;
        }
        catch (JsonException)
        {
            return default;
        }
    }

    /// <summary>The journal entries about holds of the wall a person made under one label.</summary>
    private static async Task<List<(string Key, string? Before, string? After, DateTimeOffset When)>> EntriesAsync(
        BlocwerkDbContext db, WallPanel panel, string label, ChangeJournalOp op, CancellationToken ct)
    {
        var panelText = panel.Id.ToString();
        var rows = await (
            from entry in db.ChangeJournalEntries.AsNoTracking()
            join batch in db.ChangeJournalBatches.AsNoTracking() on entry.BatchId equals batch.Id
            where batch.Label == label && batch.Actor != null && batch.ScopeId == panel.WallId
                  && entry.EntityType == nameof(Hold) && entry.Op == op
                  && ((entry.BeforeJson != null && entry.BeforeJson.Contains(panelText)) || (entry.AfterJson != null && entry.AfterJson.Contains(panelText)))
            orderby entry.CreatedAt
            select new { entry.KeyJson, entry.BeforeJson, entry.AfterJson, entry.CreatedAt }).ToListAsync(ct);
        return [.. rows.Select(r => (r.KeyJson, r.BeforeJson, r.AfterJson, r.CreatedAt))];
    }

    /// <summary>Position, size and photo of a journalled hold row; null when the row cannot be read.</summary>
    private static (Guid? PanelId, int Generation, double X, double Y, double Radius)? Read(string? json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (!root.TryGetProperty(nameof(Hold.X), out var x) || !root.TryGetProperty(nameof(Hold.Y), out var y) || !root.TryGetProperty(nameof(Hold.Radius), out var radius))
            {
                return null;
            }

            Guid? panel = root.TryGetProperty(nameof(Hold.WallPanelId), out var p) && p.ValueKind == JsonValueKind.String && Guid.TryParse(p.GetString(), out var id) ? id : null;
            int generation = root.TryGetProperty(nameof(Hold.Generation), out var g) && g.TryGetInt32(out var value) ? value : 0;
            return (panel, generation, x.GetDouble(), y.GetDouble(), radius.GetDouble());
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }
}
