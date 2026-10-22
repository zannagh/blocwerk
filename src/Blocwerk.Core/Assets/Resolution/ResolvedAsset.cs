// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.Assets.Resolution;

/// <summary>
/// The stored row a wall's content hash names, as read from the database when the request came in. Metadata only:
/// the bytes are read by <see cref="IAssetResolver.LoadAsync"/>, which checks them against <see cref="Sha256"/>.
/// </summary>
/// <param name="Kind">Which column holds the bytes.</param>
/// <param name="RowId">Id of the row (panel, wall, reset, image, texture, splat or video).</param>
/// <param name="Sha256">The 32-byte hash the request named, which the row's hash column holds.</param>
/// <param name="ContentType">Content type to serve the bytes under.</param>
/// <param name="Length">Size of the stored bytes as the row records it (used for HEAD).</param>
/// <param name="PhysicalPath">For a file-backed kind, the file on disk; null for a database blob.</param>
public sealed record ResolvedAsset(
    AssetKind Kind,
    Guid RowId,
    byte[] Sha256,
    string ContentType,
    long Length,
    string? PhysicalPath = null)
{
    /// <summary>The hash as lowercase hex, the form it takes in URLs and ETags.</summary>
    public string Hex => ContentHash.ToHex(Sha256)!;

    /// <summary>Whether a width ladder (<c>ImageVariants.Widths</c>) applies: only the photos are rescaled.</summary>
    public bool HasVariants => Kind is AssetKind.Panel or AssetKind.WallPhoto or AssetKind.ResetPhoto or AssetKind.Gallery;

    /// <summary>The wall the request named, which the row belongs to (set by the resolver).</summary>
    public Guid WallId { get; init; }

    /// <summary>
    /// Whether the response must not be stored anywhere (<c>Cache-Control: no-store</c>, no validator): a kiosk
    /// session, anonymous or act-as, asking for a kind outside the manifest's Kiosk class. The tablet's caches are
    /// shared by every identity on it, so what the anonymous tablet may not fetch must never land in them.
    /// </summary>
    public bool NoStore { get; init; }

    /// <summary>Whether the route streams the file with Range support instead of buffering it (splats only).</summary>
    public bool IsStreamed => Kind == AssetKind.Splat;
}
