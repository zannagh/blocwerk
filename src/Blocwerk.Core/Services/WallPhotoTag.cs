namespace Blocwerk.Core.Services;

/// <summary>
/// Identifies the bytes of a stored image without reading them. Every image in this app is a
/// Postgres blob served through the app, so the browser refetching a wall page costs megabytes;
/// the byte routes build an ETag out of this and answer a matching <c>If-None-Match</c> with 304
/// before the blob is ever selected. <see cref="Length"/> comes back as a server-side
/// <c>length(bytea)</c>, so obtaining a tag never moves image data.
/// </summary>
/// <param name="Length">Size of the stored bytes.</param>
/// <param name="ContentType">Stored content type, or null when the row never recorded one.</param>
/// <param name="Version">
/// A number that moves whenever these bytes are replaced — the wall/panel generation for a live
/// photo, the staging timestamp for a staged one.
/// <para>
/// LOAD-BEARING INVARIANT, and a latent sharp edge. <c>Wall</c> carries no <c>UpdatedAt</c> and no
/// rowversion, so for a live wall photo this is <c>Wall.CurrentGeneration</c> — and
/// <c>WallService.UploadPhotoAsync</c> replaces <c>Wall.Photo</c> WITHOUT bumping it. Every
/// currently reachable path that replaces a live photo happens to go through a reset or a big
/// update, both of which do bump the generation, so the tag is correct today by coincidence of
/// call graph rather than by construction. <see cref="Length"/> is included in every tag built
/// from this record precisely to blunt that: a replacement photo of a different size still moves
/// the tag. What remains uncovered is a same-generation replacement whose bytes are byte-for-byte
/// the same LENGTH and content type — that would serve a stale image, and would need a column to
/// fix properly. Anyone adding a new path that writes <c>Wall.Photo</c> must bump the generation.
/// </para>
/// <para>
/// The variant cache (<c>IImageVariantCache</c>) keys off exactly the parts this record feeds into
/// the ETag, so it inherits this invariant and nothing worse: wherever the 304 would be wrong, the
/// cached rendition is wrong in the same way and for the same reason.
/// </para>
/// </param>
/// <param name="IsArchived">
/// True for a retired generation's photo, which can never change again and may therefore be cached
/// by the browser without revalidating.
/// </param>
public sealed record WallPhotoTag(int Length, string? ContentType, long Version, bool IsArchived);
