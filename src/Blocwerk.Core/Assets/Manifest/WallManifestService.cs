// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Contracts.Assets;
using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Assets.Resolution;
using Blocwerk.Core.Data;
using Blocwerk.Core.Feed;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Assets.Manifest;

/// <summary>
/// Serves <c>GET /api/walls/{wallId}/manifest</c>: decides the caller's <see cref="ManifestAudience"/> with the very gate
/// the <c>/a/</c> routes use, then answers from <see cref="WallManifestCache"/> or builds (design §6.4, "computed lazily").
/// </summary>
/// <remarks>
/// A cached entry is reused only when the wall's feed since its cursor holds nothing that can change a manifest input
/// (<see cref="ManifestFreshness"/>) and it is younger than <see cref="MaxAge"/>. The age limit is the safety net for the
/// inputs that write no feed row yet (a re-texture of an unchanged model, bulk writes before PR 1.3); an entry that still
/// lists unhashed rows lives only <see cref="PendingMaxAge"/>, so it picks the backfill's hashes up soon.
/// </remarks>
public sealed class WallManifestService(
    IDbContextFactory<BlocwerkDbContext> dbContextFactory,
    ICurrentUserService currentUserService,
    WallManifestBuilder builder,
    WallManifestCache cache,
    IWallFeed feed,
    TimeProvider timeProvider,
    ILogger<WallManifestService> logger,
    IKioskContext? kioskContext = null)
{
    /// <summary>How long a complete manifest is reused at most.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(10);

    /// <summary>How long a manifest that still lists unhashed rows is reused at most.</summary>
    public static readonly TimeSpan PendingMaxAge = TimeSpan.FromMinutes(1);

    /// <summary>The caller's manifest of <paramref name="wallId"/>, or null for every refusal alike (the route's 404).</summary>
    public async Task<WallManifest?> GetAsync(Guid wallId, string? shareToken, CancellationToken ct)
    {
        var audience = await AudienceAsync(wallId, shareToken, ct);
        if (audience is not { } cls)
        {
            return null;
        }

        if (cache.TryGet(wallId, cls, out var cached) && await StillHoldsAsync(wallId, cached, ct) is { } held)
        {
            cache.Set(wallId, cls, held);
            return ForAudience(held, cls);
        }

        var cursor = await CurrentCursorAsync(ct);
        var manifest = await builder.BuildAsync(wallId, cls, ct);
        if (manifest is null)
        {
            return null;
        }

        var entry = new CachedManifest(manifest, cursor, timeProvider.GetUtcNow());
        if (cursor is not null)
        {
            cache.Set(wallId, cls, entry);
        }

        return ForAudience(entry, cls);
    }

    /// <summary>The caller's class on <paramref name="wallId"/>, or null when it gets no manifest.</summary>
    public async Task<ManifestAudience?> AudienceAsync(Guid wallId, string? shareToken, CancellationToken ct)
    {
        if (kioskContext is not null)
        {
            await kioskContext.InitializeAsync();
        }

        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        var access = await AssetAccessGate.DecideAsync(db, wallId, shareToken, currentUserService, kioskContext, ct);
        return ManifestAudiences.From(access, kioskContext, wallId);
    }

    // The cursor (epoch id and xid positions) is for a member's catch-up from the feed after loading (§5.8). Share-link and
    // kiosk viewers do not read the feed by cursor; they refetch on wall.manifest, so they get no cursor at all.
    private static WallManifest ForAudience(CachedManifest entry, ManifestAudience audience) =>
        audience == ManifestAudience.Member ? entry.Served : entry.Manifest;

    private async Task<CachedManifest?> StillHoldsAsync(Guid wallId, CachedManifest cached, CancellationToken ct)
    {
        var age = timeProvider.GetUtcNow() - cached.BuiltAt;
        if (cached.Cursor is not { } cursor || age >= (cached.Manifest.Pending > 0 ? PendingMaxAge : MaxAge))
        {
            return null;
        }

        try
        {
            return await ManifestFreshness.CheckAsync(feed, wallId, cursor, ct) is { } next ? cached with { Cursor = next } : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not read the feed of wall {WallId}; rebuilding its manifest", wallId);
            return null;
        }
    }

    private async Task<FeedCursor?> CurrentCursorAsync(CancellationToken ct)
    {
        try
        {
            return await feed.CurrentAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Served without a cursor and not cached: a client simply fetches it again.
            logger.LogWarning(ex, "Could not read the feed cursor; the manifest is served uncached");
            return null;
        }
    }
}
