// <copyright file="PhotoRegistrar.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Diagnostics;
using Blocwerk.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Geometry.TextureRegistration;

/// <summary>
/// Registers one photo onto every facet texture: first each on its own (the matcher's coarse search; a weak result
/// is matched again with the deterministic <see cref="RegistrationAttempts"/> within a small time budget and the best
/// kept), then every facet that failed once more, matched around the view <see cref="FacetViewPrediction"/> derives
/// from the best accepted facet of the same photo — until no further facet is gained. A facet whose texture has
/// too little of its own to be found in a whole photo (a narrow kickboard, a small side panel next to a
/// large one) is then searched only where the model says it must be. A facet the coarse search misses or finds only
/// weakly (<see cref="RegistrationAttempts.IsStrong"/>) but the caller knows <see cref="PlaneAnchor"/>s on (the photo's
/// holds as placed on an earlier model, holds linked to another photo placed in the same run) is also searched around
/// the view those anchors predict (<see cref="AnchorSeed"/>), and the better registration kept. Acceptance is the same
/// for all of them.
/// </summary>
/// <param name="session">The decoded photo.</param>
/// <param name="logger">Where each registration is logged.</param>
/// <param name="label">The photo's name in the log (also what the attempts' seeds derive from).</param>
/// <param name="focalPx">The photo's focal length when known (EXIF), used only when the anchor cannot tell it.</param>
/// <param name="anchors">Known plane positions of photo points, to seed facets the coarse search misses; null for none.</param>
/// <param name="direct">The photo's registrations from an earlier pass, the starting point instead of matching again (weak ones are still seeded); null matches.</param>
/// <param name="attemptBudget">The time the retries of one weak facet may take; null is <see cref="RegistrationAttempts.DefaultBudget"/>.</param>
public sealed class PhotoRegistrar(
    IPhotoTextureSession session,
    ILogger logger,
    string label,
    double? focalPx = null,
    IReadOnlyList<PlaneAnchor>? anchors = null,
    IReadOnlyList<FacetRegistration>? direct = null,
    TimeSpan? attemptBudget = null)
{
    /// <summary>Registers the photo onto every texture.</summary>
    /// <param name="textures">The model's textures.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>One registration per texture, in their order.</returns>
    public List<FacetRegistration> RegisterAll(IReadOnlyList<RegistrationTexture> textures, CancellationToken ct = default)
    {
        var reuse = direct is { } d && d.Count == textures.Count && d.Select(r => r.FacetId).SequenceEqual(textures.Select(t => t.Frame.FacetId));
        var results = new List<FacetRegistration>();
        foreach (var texture in textures)
        {
            ct.ThrowIfCancellationRequested();
            results.Add(reuse ? direct![results.Count] : Direct(texture, ct));
        }

        for (var i = 0; i < textures.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            if (!RegistrationAttempts.IsStrong(results[i]) && Anchored(textures[i]) is { } anchored && RegistrationAttempts.Better(anchored, results[i]))
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

    /// <summary>
    /// The coarse-search registration: the plain match, and when that found the facet but only weakly the <see cref="RegistrationAttempts.Retries"/>
    /// until one is strong or the budget is spent; the best is kept.
    /// </summary>
    private FacetRegistration Direct(RegistrationTexture texture, CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        var best = Match(texture, null, PhotoTextureAttempt.Default);
        var attempts = 1;
        var budget = attemptBudget ?? RegistrationAttempts.DefaultBudget;
        var retries = Stopwatch.StartNew();
        foreach (var attempt in RegistrationAttempts.Retries(label, texture.Frame.FacetId))
        {
            if (!RegistrationAttempts.WorthRetrying(best) || retries.Elapsed >= budget)
            {
                break;
            }

            ct.ThrowIfCancellationRequested();
            var r = Match(texture, null, attempt);
            attempts++;
            best = RegistrationAttempts.Better(r, best) ? r : best;
        }

        Log(best, attempts > 1 ? $" (best of {attempts} attempts)" : null, watch.Elapsed);
        return best;
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

    /// <summary>One seeded photo × texture match, logged.</summary>
    private FacetRegistration Register(RegistrationTexture texture, double[] seed, string seededBy)
    {
        var watch = Stopwatch.StartNew();
        var r = Match(texture, seed, PhotoTextureAttempt.Default);
        Log(r, seededBy, watch.Elapsed);
        return r;
    }

    /// <summary>One photo × texture match; a matcher failure on one texture only costs that facet.</summary>
    private FacetRegistration Match(RegistrationTexture texture, double[]? seed, PhotoTextureAttempt attempt)
    {
        try
        {
            var match = session.Match(texture.Image, texture.Mask, (texture.Frame.MmPerPxA + texture.Frame.MmPerPxB) / 2, seed, attempt);
            return PhotoTextureRegistration.Register(match, session.Width, session.Height, texture.Frame, texture.Extent);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            logger.LogWarning(ex, "Panel {Panel} × facet {FacetId}: matching failed", label, texture.Frame.FacetId);
            return FacetRegistration.Rejected(texture.Frame.FacetId, texture.Extent, 0, 0, "matching failed");
        }
    }

    private void Log(FacetRegistration r, string? how, TimeSpan took) =>
        logger.LogInformation(
            "Panel {Panel} × facet {FacetId}{Seed}: {Matches} matches (coarse {Coarse}), {Inliers} inliers, coverage {Coverage:P0} "
            + "(photo {Share:P0}), RMS {Rms:F1} mm, {Seconds:F1} s — {Verdict}",
            label, r.FacetId, how ?? string.Empty, r.Matches, r.CoarseInliers, r.Inliers,
            r.Coverage, r.PhotoShare, r.RmsMm ?? double.NaN, took.TotalSeconds, r.Accepted ? "accepted" : r.Reason);
}
