// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Authentication.Authorization;
using Blocwerk.Contracts.Assets;
using Blocwerk.Contracts.Documents;
using Blocwerk.Contracts.Mine;
using Blocwerk.Core.Assets.Mine;
using Blocwerk.Core.Assets.Partition;
using Blocwerk.Web.Endpoints.Feed;
using Microsoft.AspNetCore.Mvc;

namespace Blocwerk.Web.Endpoints;

/// <summary>
/// The reader's own ticks and favourites of one wall (Phase 3 plan §1, PR 3.4), which a signed-in device keeps in its
/// <c>u-</c> partition for the offline client.
/// </summary>
/// <remarks>
/// <para>
/// A signed-in human only: the default policy (the cookie challenge answers a signed-out caller 401), API-key principals
/// rejected, and the route sits under <c>/api/me</c>, outside the API-key prefixes, so a bearer presented there
/// authenticates nobody. <see cref="MineAccessGate"/> then refuses a kiosk tablet (403, whoever it acts as), a reader
/// whose class sees no marks online (403: a share link), an account that is gone (410) and an unknown wall (404).
/// </para>
/// <para>
/// Every answer is <c>no-store</c>: one reader's data, never kept by the browser's HTTP cache or a proxy. The device's
/// copy lives in the ledger only, where every wipe of the partition reaches it.
/// </para>
/// </remarks>
public static class MineEndpoints
{
    /// <summary><c>GET</c>: the caller's marks on the wall (<see cref="MineDocument"/>).</summary>
    public const string Route = "/api/me/walls/{wallId:guid}/mine";

    public static void MapMine(this WebApplication app)
    {
        app.MapGet(Route, HandleAsync)
            .RequireAuthorization()
            .DenyApiKeyPrincipals();
    }

    internal static async Task<IResult> HandleAsync(
        Guid wallId,
        HttpContext http,
        [FromServices] MineDocumentService mine,
        [FromServices] ICachePartitionResolver partitions,
        CancellationToken ct)
    {
        var access = await mine.AccessAsync(wallId, FeedRequestAccess.SessionOf(http), ct);
        if (access is not { UserId: { } userId, Audience: { } audience })
        {
            if (access.WallMissing)
            {
                FeedRequestAccess.NoStore(http);
                return Results.NotFound();
            }

            return FeedRequestAccess.Refused(http, access.Refusal);
        }

        var document = await mine.BuildAsync(wallId, userId, audience, await PartitionOfAsync(partitions), ct);
        FeedRequestAccess.NoStore(http);
        return Results.Json(document, DataDocumentSchema.Options);
    }

    // The label the device keys the marks by. A fault answers "unknown", which no tab caches under, so it stores nothing.
    private static async Task<string> PartitionOfAsync(ICachePartitionResolver partitions)
    {
        try
        {
            return await partitions.ResolveAsync();
        }
        catch (Exception)
        {
            return CachePartitions.Unknown;
        }
    }
}
