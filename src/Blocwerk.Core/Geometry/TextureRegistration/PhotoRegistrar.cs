// <copyright file="PhotoRegistrar.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Diagnostics;
using Blocwerk.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Geometry.TextureRegistration;

/// <summary>
/// Registers one photo onto every facet texture: first each on its own (the matcher's coarse search), then
/// every facet that failed once more, matched around the view <see cref="FacetViewPrediction"/> derives from
/// the best accepted facet of the same photo — until no further facet is gained. A facet whose texture has
/// too little of its own to be found in a whole photo (a narrow kickboard, a small side panel next to a
/// large one) is then searched only where the model says it must be. A facet the coarse search misses but the
/// caller knows <see cref="PlaneAnchor"/>s on (the photo's holds as placed on an earlier model, holds linked to
/// another photo placed in the same run) is first searched around the view those anchors predict
/// (<see cref="AnchorSeed"/>). Acceptance is the same for all of them.
/// </summary>
/// <param name="session">The decoded photo.</param>
/// <param name="logger">Where each registration is logged.</param>
/// <param name="label">The photo's name in the log.</param>
/// <param name="focalPx">The photo's focal length when known (EXIF), used only when the anchor cannot tell it.</param>
/// <param name="anchors">Known plane positions of photo points, to seed facets the coarse search misses; null for none.</param>
public sealed class PhotoRegistrar(
    IPhotoTextureSession session, ILogger logger, string label, double? focalPx = null, IReadOnlyList<PlaneAnchor>? anchors = null)
{
    /// <summary>Registers the photo onto every texture.</summary>
    /// <param name="textures">The model's textures.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>One registration per texture, in their order.</returns>
    public List<FacetRegistration> RegisterAll(IReadOnlyList<RegistrationTexture> textures, CancellationToken ct = default)
    {
        var results = new List<FacetRegistration>();
        foreach (var texture in textures)
        {
            ct.ThrowIfCancellationRequested();
            results.Add(Register(texture, null, null));
        }

        for (var i = 0; i < textures.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            if (!results[i].Accepted && Anchored(textures[i]) is { } anchored && (anchored.Accepted || anchored.Inliers > results[i].Inliers))
            {
                results[i] = anchored;
            }
        }

        var tried = new HashSet<(int Target, int Anchor)>();
        bool gained;
        do
        {
            gained = false;
            for (var i = 0; i < textures.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                if (!results[i].Accepted && Seeded(textures, results, i, tried) is { } seeded)
                {
                    gained |= seeded.Accepted;
                    results[i] = seeded.Accepted || seeded.Inliers > results[i].Inliers ? seeded : results[i];
                }
            }
        }
        while (gained);

        return results;
    }

    /// <summary>The texture matched around the view its anchors predict, or null when it has too few.</summary>
    private FacetRegistration? Anchored(RegistrationTexture texture)
    {
        if (anchors is not { Count: > 0 } || AnchorSeed.Fit(anchors, texture.Frame, session.Width, session.Height) is not { } seed)
        {
            return null;
        }

        return Register(texture, seed.Seed, $" (seeded from {seed.Inliers} placed holds)");
    }

    /// <summary>The target matched around its view predicted from the best untried accepted anchor, or null.</summary>
    private FacetRegistration? Seeded(
        IReadOnlyList<RegistrationTexture> textures, List<FacetRegistration> results, int target, HashSet<(int Target, int Anchor)> tried)
    {
        if (textures[target].Facet is not { } targetFacet)
        {
            return null;
        }

        var candidates = Enumerable.Range(0, textures.Count)
            .Where(j => j != target && results[j].Accepted && textures[j].Facet is not null && !tried.Contains((target, j)))
            .OrderByDescending(j => results[j].Inliers);
        foreach (var j in candidates)
        {
            tried.Add((target, j));
            var seed = FacetViewPrediction.PhotoToTexture(
                results[j], textures[j].Facet!, targetFacet, textures[target].Frame, session.Width, session.Height, focalPx);
            if (seed is not null)
            {
                return Register(textures[target], seed, $" (predicted from facet {results[j].FacetId})");
            }
        }

        return null;
    }

    /// <summary>One photo × texture; a matcher failure on one texture only costs that facet.</summary>
    private FacetRegistration Register(RegistrationTexture texture, double[]? seed, string? seededBy)
    {
        var watch = Stopwatch.StartNew();
        FacetRegistration r;
        try
        {
            var match = session.Match(texture.Image, texture.Mask, (texture.Frame.MmPerPxA + texture.Frame.MmPerPxB) / 2, seed);
            r = PhotoTextureRegistration.Register(match, session.Width, session.Height, texture.Frame, texture.Extent);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            logger.LogWarning(ex, "Panel {Panel} × facet {FacetId}: matching failed", label, texture.Frame.FacetId);
            r = FacetRegistration.Rejected(texture.Frame.FacetId, texture.Extent, 0, 0, "matching failed");
        }

        logger.LogInformation(
            "Panel {Panel} × facet {FacetId}{Seed}: {Matches} matches (coarse {Coarse}), {Inliers} inliers, coverage {Coverage:P0} "
            + "(photo {Share:P0}), RMS {Rms:F1} mm, {Seconds:F1} s — {Verdict}",
            label, r.FacetId, seededBy ?? string.Empty, r.Matches, r.CoarseInliers, r.Inliers,
            r.Coverage, r.PhotoShare, r.RmsMm ?? double.NaN, watch.Elapsed.TotalSeconds, r.Accepted ? "accepted" : r.Reason);
        return r;
    }
}
