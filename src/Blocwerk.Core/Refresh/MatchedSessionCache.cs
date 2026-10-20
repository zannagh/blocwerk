// <copyright file="MatchedSessionCache.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Blocwerk.Core.Data;
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
    private readonly ConcurrentDictionary<Guid, Entry> entries = new();

    private sealed record Entry(string Fingerprint, bool Use3D, BigUpdateSession Session);

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

        var session = await match();

        // Fingerprinted after the match: the matcher's own writes (discard reasons) are not among the inputs.
        entries[wallId] = new Entry(await FingerprintAsync(db, wallId), use3D, session);
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
    internal static async Task<string> FingerprintAsync(BlocwerkDbContext db, Guid wallId)
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

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())), 0, 16);
    }

    private static string Shape(List<Entities.ShapePoint>? points) =>
        points is null ? "-" : string.Join(';', points.Select(p => string.Create(CultureInfo.InvariantCulture, $"{p.Dx:R},{p.Dy:R}")));
}
