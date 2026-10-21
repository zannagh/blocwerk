// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.Assets.Resolution;

/// <summary>
/// Maps <c>(wallId, sha)</c> to the stored row whose hash column holds that sha, for the caller of this request.
/// Hashes are always read from the database, never from a circuit's cached wall: a cached wall can still hold the
/// null hashes it was loaded with before the SQL backfill filled them in.
/// </summary>
public interface IAssetResolver
{
    /// <summary>
    /// The row of <paramref name="wallId"/> whose content hash is <paramref name="sha"/>, if the caller may read it;
    /// otherwise null. Null covers every refusal alike (no access, a sha of another wall, a sha no row holds any
    /// more, a malformed sha), so the route answers all of them with the same 404.
    /// </summary>
    /// <param name="wallId">The wall in the route.</param>
    /// <param name="sha">The hash in the route, 64 lowercase hex characters.</param>
    /// <param name="shareToken">The <c>?token=</c> of a share link, if any.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<ResolvedAsset?> ResolveAsync(Guid wallId, string sha, string? shareToken, CancellationToken ct);

    /// <summary>
    /// The bytes of <paramref name="asset"/>, or null when they are gone or no longer hash to its sha (a rewrite
    /// that landed after <see cref="ResolveAsync"/>). The route never serves bytes this did not check.
    /// </summary>
    Task<byte[]?> LoadAsync(ResolvedAsset asset, CancellationToken ct);

    /// <summary>
    /// Whether the file of a streamed asset (<see cref="ResolvedAsset.IsStreamed"/>) still hashes to its sha. The
    /// result is remembered per file length and write time, so a splat is hashed once, not on every Range request.
    /// </summary>
    Task<bool> VerifyFileAsync(ResolvedAsset asset, CancellationToken ct);
}
