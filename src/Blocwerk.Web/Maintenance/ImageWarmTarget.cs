using Blocwerk.Core.Abstractions;

namespace Blocwerk.Web.Maintenance;

/// <summary>
/// One image the variant pipeline can serve, reduced to what warming needs: where its renditions
/// are cached, and how to get the stored original if any of them are missing.
/// </summary>
/// <param name="Key">
/// The cache key, built by <c>ImageResponse</c> from the same parts a request would use.
/// </param>
/// <param name="Description">Human-readable identification, for the log.</param>
/// <param name="Load">
/// Reads the stored original. Called at most once per image and only on a miss, so a re-run over an
/// already-warm cache never touches Postgres or the file store.
/// </param>
public sealed record ImageWarmTarget(
    ImageVariantKey Key,
    string Description,
    Func<CancellationToken, Task<byte[]?>> Load);
