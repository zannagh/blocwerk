// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Text.Json.Serialization;

namespace Blocwerk.Contracts.Assets;

/// <summary>One asset of a <see cref="WallManifest"/>. The kind-specific fields are null where they do not apply.</summary>
public sealed record ManifestAsset
{
    /// <summary>
    /// Gets the content hash (64 lowercase hex characters), or null while the row is not hashed yet. Always written,
    /// null included: a null sha means "not addressable yet, use the legacy URL network-only" (design §6.10).
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? Sha { get; init; }

    /// <summary>Gets the kind (<see cref="ManifestAssetKinds"/>).</summary>
    public required string Kind { get; init; }

    /// <summary>Gets the id of the row holding the bytes (panel, wall, reset, image, texture, splat or video).</summary>
    public required Guid Id { get; init; }

    /// <summary>Gets the grid column of a panel.</summary>
    public int? Col { get; init; }

    /// <summary>Gets the grid row of a panel.</summary>
    public int? Row { get; init; }

    /// <summary>Gets the generation of a panel or reset photo.</summary>
    public int? Gen { get; init; }

    /// <summary>Gets the facet of a texture or mask.</summary>
    public string? Facet { get; init; }

    /// <summary>Gets the splat count of a splat file, which is how a device picks the one level it renders.</summary>
    public int? Splats { get; init; }

    /// <summary>Gets the boulder of a video thumbnail.</summary>
    public Guid? BoulderId { get; init; }

    /// <summary>Gets the content type of the original.</summary>
    public string? Type { get; init; }

    /// <summary>Gets the size of the original in bytes, as the row records it.</summary>
    public long Bytes { get; init; }

    /// <summary>Gets when a gallery image was uploaded: the client's background fill takes the newest first.</summary>
    public DateTimeOffset? At { get; init; }

    /// <summary>Gets the rendition widths (<c>/a/{sha}/{width}</c>) of a photo kind; null for the others.</summary>
    public IReadOnlyList<int>? Widths { get; init; }

    /// <summary>Gets the tier (<see cref="ManifestTiers"/>).</summary>
    public required string Tier { get; init; }
}
