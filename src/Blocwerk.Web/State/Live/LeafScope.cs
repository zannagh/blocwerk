// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Text.Json;
using Blocwerk.Contracts.Feed;
using Blocwerk.Core.Feed;

namespace Blocwerk.Web.State.Live;

/// <summary>
/// The part of a wall's feed one live leaf list shows: its wall, its boulder (null for a wall-level list) and its kinds.
/// <see cref="Select"/> turns a delivery into the list's <see cref="LeafBatch"/>, or null when nothing in it concerns the
/// list. Only what the circuit's subscription delivered can be selected, so the audiences decide what a list may show.
/// </summary>
/// <param name="WallId">The wall.</param>
/// <param name="BoulderId">The boulder of a boulder-level list; null for a wall-level one.</param>
/// <param name="Kinds">The kinds the list patches or reloads on (<c>wall.resync</c> always reloads).</param>
/// <param name="CoarseKinds">
/// Kinds that only mean "the content changed, re-read" for this list (holds and volumes for a 3D view): they reload like
/// <c>wall.resync</c>, coalesced, as a <see cref="LeafBatch.Structural"/> reload.
/// </param>
public sealed record LeafScope(Guid WallId, Guid? BoulderId, IReadOnlySet<string> Kinds, IReadOnlySet<string>? CoarseKinds = null)
{
    // Wall rows that concern every list of the wall, boulder-level ones too.
    private static readonly HashSet<string> WallWide = [FeedKinds.WallResync, FeedKinds.UserProfile, FeedKinds.View3DChanged];

    /// <summary>
    /// The list's batch of <paramref name="delivery"/>: events of its kinds and boulder that are newer than
    /// <paramref name="loadedAt"/> (the list's <c>CursorAtLoad</c>; an older event is already in it), or a reload.
    /// </summary>
    public LeafBatch? Select(FeedDelivery delivery, FeedCursor? loadedAt)
    {
        if (delivery.WallId != WallId)
        {
            return null;
        }

        if (delivery.Resync)
        {
            return LeafBatch.ReloadOnly;
        }

        var events = new List<FeedEnvelope>();
        foreach (var e in delivery.Events)
        {
            if (!Concerns(e))
            {
                continue;
            }

            if (!FeedCursor.TryParse(e.Cursor, out var cursor) || !IsSupported(e.Data))
            {
                return LeafBatch.Coarse;
            }

            if (loadedAt is { } at && at.Epoch == cursor.Epoch && at.Position >= cursor.Position)
            {
                continue; // the load already has it
            }

            if (e.Kind == FeedKinds.WallResync || CoarseKinds?.Contains(e.Kind) == true)
            {
                return LeafBatch.Coarse;
            }

            events.Add(e);
        }

        return events.Count == 0 ? null : new LeafBatch(events, false);
    }

    private bool Concerns(FeedEnvelope e)
    {
        var coarse = CoarseKinds?.Contains(e.Kind) == true;
        if (e.Kind != FeedKinds.WallResync && !Kinds.Contains(e.Kind) && !coarse)
        {
            return false;
        }

        return BoulderId is null || coarse || e.BoulderId == BoulderId || (e.BoulderId is null && WallWide.Contains(e.Kind));
    }

    private static bool IsSupported(JsonElement? data) =>
        data is not { ValueKind: JsonValueKind.Object } d || !d.TryGetProperty("sv", out var sv)
        || (sv.TryGetInt32(out var version) && FeedSchema.IsSupported(version));
}
