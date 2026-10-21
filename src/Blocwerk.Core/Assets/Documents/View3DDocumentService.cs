// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Assets.Partition;
using Blocwerk.Core.Assets.Resolution;
using Blocwerk.Core.Data;
using Blocwerk.Core.Geometry.View3D;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Assets.Documents;

/// <summary>
/// Serves <c>GET /api/walls/{wallId}/d/view3d/{sha}</c> (design §6.5): the grant is <see cref="AssetAccessGate"/>'s, the
/// very gate of the <c>/a/</c> routes and the manifest; the bytes come from <see cref="View3DDocumentStore"/>, or, after a
/// restart, from a rebuild of the view as this caller may see it.
/// </summary>
/// <remarks>
/// <para>
/// A rebuilt document is served only when it hashes to the requested sha; anything else is the route's bare 404, and the
/// page falls back to the view over interop. A rebuild per wall (and boulder) runs at most once per
/// <see cref="View3DDocumentStore.RebuildWindow"/>, so a share link cannot turn random shas into builds.
/// </para>
/// <para>
/// The document is wall-level (no boulder, no viewer, no token; <see cref="View3DDocument"/>), so it is of the kiosk class:
/// a tablet that may cache its wall gets it cacheable. A kiosk session that may not (<see cref="KioskCacheRules"/>) gets it
/// <c>no-store</c>.
/// </para>
/// </remarks>
public sealed class View3DDocumentService(
    IDbContextFactory<BlocwerkDbContext> dbContextFactory,
    ICurrentUserService currentUserService,
    IWall3DViewService views,
    View3DDocumentStore store,
    TimeProvider timeProvider,
    ILogger<View3DDocumentService> logger,
    IKioskContext? kioskContext = null)
{
    /// <summary>The document, or null for every refusal alike (the route's 404).</summary>
    /// <param name="wallId">The wall in the route.</param>
    /// <param name="sha">The sha in the route.</param>
    /// <param name="shareToken">The share token, for a share link.</param>
    /// <param name="boulderId">The boulder page the document was derived from, used only to rebuild it.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The document and its cache policy, or null.</returns>
    public async Task<ServedView3DDocument?> GetAsync(Guid wallId, string sha, string? shareToken, Guid? boulderId, CancellationToken ct)
    {
        if (!AssetUrls.IsSha(sha) || await AccessAsync(wallId, shareToken, ct) == AssetAccess.None)
        {
            return null;
        }

        var noStore = kioskContext is { IsKiosk: true } && !KioskCacheRules.AllowsKioskCache(kioskContext, wallId);
        var document = store.Find(wallId, sha) ?? await RebuildAsync(wallId, sha, shareToken, boulderId, ct);
        return document is null ? null : new ServedView3DDocument(document, noStore);
    }

    private async Task<AssetAccess> AccessAsync(Guid wallId, string? shareToken, CancellationToken ct)
    {
        if (kioskContext is not null)
        {
            await kioskContext.InitializeAsync();
        }

        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        return await AssetAccessGate.DecideAsync(db, wallId, shareToken, currentUserService, kioskContext, ct);
    }

    private async Task<StoredView3DDocument?> RebuildAsync(Guid wallId, string sha, string? shareToken, Guid? boulderId, CancellationToken ct)
    {
        if (!store.TryBeginRebuild(wallId, boulderId, timeProvider.GetUtcNow()))
        {
            return null;
        }

        Wall3DViewResult result;
        try
        {
            result = await views.BuildAsync(wallId, boulderId, shareToken, ct);
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }

        if (result is not { Status: Wall3DViewStatus.Ok, View: { } view })
        {
            return null;
        }

        var published = store.Publish(view);
        if (published.Sha != sha)
        {
            logger.LogInformation("View3d document {Sha} of wall {WallId} is not the current one; answered 404", sha, wallId);
            return null;
        }

        return store.Find(wallId, sha);
    }
}
