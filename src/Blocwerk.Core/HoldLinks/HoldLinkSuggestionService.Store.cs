// <copyright file="HoldLinkSuggestionService.Store.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Collections.Concurrent;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.HoldLinks;

/// <summary>Finding the suggestions again and storing them, one wall at a time.</summary>
public sealed partial class HoldLinkSuggestionService
{
    /// <summary>One refresh, link or rejection per wall at a time (single-instance app).</summary>
    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> Gates = new();

    /// <summary>Per wall, the inputs of the last stored refresh (<see cref="HoldLinkInputs.Fingerprint"/>).</summary>
    private static readonly ConcurrentDictionary<Guid, string> Fingerprints = new();

    private static SemaphoreSlim GateOf(Guid wallId) => Gates.GetOrAdd(wallId, _ => new SemaphoreSlim(1, 1));

    /// <summary>
    /// Finds and stores the wall's suggestions; returns how many are pending, null without a usable model. The pairwise
    /// search and the writes are skipped when nothing it depends on changed since the last refresh. A write that loses
    /// against a concurrent change is logged and answered with null; the next refresh starts over.
    /// </summary>
    private async Task<int?> RefreshAsync(Guid wallId, CancellationToken ct)
    {
        var gate = GateOf(wallId);
        await gate.WaitAsync(ct);
        try
        {
            await using var db = await dbContextFactory.CreateDbContextAsync(ct);
            var inputs = await HoldLinkCandidateLoader.LoadAsync(db, wallId, ct);
            var stored = await db.HoldLinkSuggestions.Where(s => s.WallId == wallId).ToListAsync(ct);
            if (inputs is null)
            {
                Fingerprints.TryRemove(wallId, out _);
                Apply(db, wallId, stored, [], null);
                await db.SaveChangesAsync(ct);
                return null;
            }

            var links = await HoldLinkCandidateLoader.LinksAsync(db, wallId, ct);
            var rejected = stored.Where(s => s.Status == HoldLinkSuggestionStatus.Rejected).Select(s => (s.HoldAId, s.HoldBId)).ToHashSet();
            var fingerprint = inputs.Fingerprint(links, rejected);
            if (Fingerprints.TryGetValue(wallId, out var last) && last == fingerprint)
            {
                return stored.Count(s => s.Status == HoldLinkSuggestionStatus.Pending);
            }

            var found = HoldLinkSuggestionFinder.Find(inputs.Placed, links, rejected, inputs.PanelOf);
            Apply(db, wallId, stored, found, inputs.Live);
            await db.SaveChangesAsync(ct);
            Fingerprints[wallId] = fingerprint;
            return found.Count;
        }
        catch (DbUpdateException ex)
        {
            Fingerprints.TryRemove(wallId, out _);
            logger.LogWarning(ex, "Hold link suggestions on wall {WallId} could not be stored; the next refresh retries", wallId);
            return null;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Makes the pending rows exactly <paramref name="found"/> (kept rows keep their id and date); drops answered rows
    /// of holds that are no longer live. Without a model (<paramref name="live"/> null) only the pending rows go.
    /// </summary>
    private static void Apply(
        BlocwerkDbContext db, Guid wallId, List<HoldLinkSuggestion> stored, List<HoldLinkPairSuggestion> found, IReadOnlySet<Guid>? live)
    {
        var wanted = found.ToDictionary(f => (f.HoldAId, f.HoldBId));
        foreach (var row in stored)
        {
            var gone = live is not null && (!live.Contains(row.HoldAId) || !live.Contains(row.HoldBId));
            if (row.Status == HoldLinkSuggestionStatus.Pending && wanted.Remove((row.HoldAId, row.HoldBId), out var f) && !gone)
            {
                row.DistanceMm = f.DistanceMm;
            }
            else if (row.Status == HoldLinkSuggestionStatus.Pending || gone)
            {
                db.HoldLinkSuggestions.Remove(row);
            }
        }

        foreach (var f in wanted.Values)
        {
            db.HoldLinkSuggestions.Add(new HoldLinkSuggestion
            {
                WallId = wallId,
                HoldAId = f.HoldAId,
                HoldBId = f.HoldBId,
                DistanceMm = f.DistanceMm,
                Status = HoldLinkSuggestionStatus.Pending,
            });
        }
    }

    /// <summary>Runs <paramref name="action"/> holding the wall's lock (links and rejections never race a refresh).</summary>
    private static async Task UnderGateAsync(Guid wallId, Func<Task> action, CancellationToken ct)
    {
        var gate = GateOf(wallId);
        await gate.WaitAsync(ct);
        try
        {
            await action();
        }
        finally
        {
            gate.Release();
        }
    }
}
