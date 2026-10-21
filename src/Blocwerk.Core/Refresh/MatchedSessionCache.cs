// <copyright file="MatchedSessionCache.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Blocwerk.Core.Data;
using Blocwerk.Core.Feed;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Refresh;

/// <summary>
/// The last matched session per wall update, so the summary after a review answer does not run the matcher again (that
/// took ~50 s per answer on a big wall). The matcher reads only the holds, the panels, the wall's generation and the
/// session's unaligned panels; an answer changes none of them. Every lookup fingerprints exactly those inputs, so an
/// edit anywhere (a touch-up, the wall editor, another staging) is a miss and the matcher runs as before. Only the
/// preview uses it: Apply always matches afresh and compares the confirmed version. One entry per wall, per process
/// (the app runs as one instance).
/// </summary>
public sealed class MatchedSessionCache
{
    /// <summary>How long the holds-and-panels part of a fingerprint is reused while no change of the wall was published.</summary>
    public static readonly TimeSpan GeometryReuse = TimeSpan.FromSeconds(30);

    private readonly ConcurrentDictionary<Guid, Entry> entries = new();
    private readonly ConcurrentDictionary<Guid, Geometry> geometry = new();
    private readonly ConcurrentDictionary<Guid, long> versions = new();
    private readonly bool notified;

    /// <summary>Initializes a new instance of the <see cref="MatchedSessionCache"/> class.</summary>
    /// <param name="commits">
    /// The in-process own-write path: every committed feed row of a wall counts as a change of it, synchronously at commit
    /// (a hold, a panel, a staged photo: <c>panel.staged</c>, a wall).
    /// </param>
    /// <param name="feed">
    /// The live feed's hub: every wake of a wall (a commit that wrote feed rows for it) also counts as a change, shortly
    /// after the commit. With either, the expensive part of a fingerprint (every hold of the wall) is read once per change
    /// and reused for <see cref="GeometryReuse"/>; with neither, it is read on every lookup.
    /// </param>
    public MatchedSessionCache(FeedCommits? commits = null, IWallFeedHub? feed = null)
    {
        // Never disposed: the cache is a singleton beside the observers, so the observations live exactly as long.
        if (commits is not null)
        {
            notified = true;
            _ = commits.Observe(OnCommitted);
        }

        if (feed is not null)
        {
            notified = true;
            _ = feed.ObserveWakes(Bump);
        }
    }

    private sealed record Entry(string Fingerprint, bool Use3D, BigUpdateSession Session);

    private sealed record Geometry(long Version, DateTimeOffset At, string Text);

    /// <summary>The cached session when its inputs are unchanged, else the matcher's (which is then cached).</summary>
    /// <param name="db">A context to read the fingerprint with.</param>
    /// <param name="wallId">The wall.</param>
    /// <param name="use3D">Whether the 3D evidence is wanted (a session with it also serves one without).</param>
    /// <param name="match">Runs the matcher.</param>
    /// <returns>The matched session.</returns>
    public async Task<BigUpdateSession> GetOrMatchAsync(BlocwerkDbContext db, Guid wallId, bool use3D, Func<Task<BigUpdateSession>> match)
    {
        if (await PeekAsync(db, wallId, use3D) is { } hit)
        {
            return hit;
        }

        // Fingerprinted before the match: an edit made while it runs (~50 s) makes the entry a miss, never a stale hit. The
        // matcher's own writes (discard reasons) are not among the inputs.
        var fingerprint = await FingerprintAsync(db, wallId);
        var session = await match();
        entries[wallId] = new Entry(fingerprint, use3D, session);
        return session;
    }

    /// <summary>The cached session when its inputs are unchanged, else null (nothing is matched).</summary>
    public async Task<BigUpdateSession?> PeekAsync(BlocwerkDbContext db, Guid wallId, bool use3D = false)
    {
        if (!entries.TryGetValue(wallId, out var entry) || (use3D && !entry.Use3D))
        {
            return null;
        }

        return entry.Fingerprint == await FingerprintAsync(db, wallId) ? entry.Session : null;
    }

    /// <summary>Forgets the wall's session (after Apply or a discard).</summary>
    public void Forget(Guid wallId) => entries.TryRemove(wallId, out _);

    /// <summary>A short fingerprint of everything the matcher reads for the wall's open update.</summary>
    internal async Task<string> FingerprintAsync(BlocwerkDbContext db, Guid wallId)
    {
        var text = new StringBuilder();
        var wall = await db.Walls.AsNoTracking().IgnoreQueryFilters().Where(w => w.Id == wallId)
            .Select(w => new { w.CurrentGeneration, w.UsesMultipleImages })
            .FirstOrDefaultAsync();
        text.Append(CultureInfo.InvariantCulture, $"w:{wall?.CurrentGeneration}:{wall?.UsesMultipleImages}\n");
        var session = await WallUpdateSessions.FindOpenAsync(db, wallId);
        var carry = string.Join(',', (session?.UnalignedCarryPanelIds ?? []).Order());
        var overlap = string.Join(',', (session?.UnalignedOverlapPanelIds ?? []).Order());
        text.Append(CultureInfo.InvariantCulture, $"s:{session?.Id}:{session?.FullRevision}:{carry}:{overlap}\n");
        text.Append(await GeometryAsync(db, wallId));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())), 0, 16);
    }

    private void Bump(Guid wallId) => versions.AddOrUpdate(wallId, 1, (_, v) => v + 1);

    private void OnCommitted(FeedCommit commit)
    {
        foreach (var wallId in commit.Events.Select(e => e.Envelope.WallId).OfType<Guid>().Distinct())
        {
            Bump(wallId);
        }
    }

    /// <summary>The panels and holds part, reused while the wall published no change (and briefly, as a safety net).</summary>
    private async Task<string> GeometryAsync(BlocwerkDbContext db, Guid wallId)
    {
        var version = versions.GetValueOrDefault(wallId);
        if (notified && geometry.TryGetValue(wallId, out var known) && known.Version == version && DateTimeOffset.UtcNow - known.At < GeometryReuse)
        {
            return known.Text;
        }

        var text = await ReadGeometryAsync(db, wallId);
        geometry[wallId] = new Geometry(version, DateTimeOffset.UtcNow, text);
        return text;
    }

    private static async Task<string> ReadGeometryAsync(BlocwerkDbContext db, Guid wallId)
    {
        var text = new StringBuilder();
        var panels = await db.WallPanels.AsNoTracking().Where(p => p.WallId == wallId)
            .Select(p => new { p.Id, p.Generation, p.Col, p.Row, Photo = p.Photo == null ? 0 : p.Photo.Length, Staged = p.StagedPhoto == null ? 0 : p.StagedPhoto.Length })
            .ToListAsync();
        foreach (var p in panels.OrderBy(p => p.Id))
        {
            text.Append(CultureInfo.InvariantCulture, $"p:{p.Id}:{p.Generation}:{p.Col}:{p.Row}:{p.Photo}:{p.Staged}\n");
        }

        var holds = await db.Holds.AsNoTracking().Where(h => h.WallId == wallId)
            .Select(h => new { h.Id, h.Generation, h.WallPanelId, h.X, h.Y, h.Radius, h.IsVirtual, h.IsAutoDetected, h.ShapePoints })
            .ToListAsync();
        foreach (var h in holds.OrderBy(h => h.Id))
        {
            text.Append(CultureInfo.InvariantCulture, $"h:{h.Id}:{h.Generation}:{h.WallPanelId}:{h.X:R}:{h.Y:R}:{h.Radius:R}:{h.IsVirtual}:{h.IsAutoDetected}:{Shape(h.ShapePoints)}\n");
        }

        return text.ToString();
    }

    private static string Shape(List<Entities.ShapePoint>? points) =>
        points is null ? "-" : string.Join(';', points.Select(p => string.Create(CultureInfo.InvariantCulture, $"{p.Dx:R},{p.Dy:R}")));
}
