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
/// for all of them. A queued session (<see cref="IAsyncPhotoTextureSession"/>) is awaited match by match through
/// <see cref="RegisterAllAsync"/>; any other session matches synchronously.
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
    /// <summary>Registers the photo onto every texture, on a session that matches synchronously.</summary>
    /// <param name="textures">The model's textures.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>One registration per texture, in their order.</returns>
    /// <exception cref="InvalidOperationException">The session is a queued one: use <see cref="RegisterAllAsync"/>.</exception>
    public List<FacetRegistration> RegisterAll(IReadOnlyList<RegistrationTexture> textures, CancellationToken ct = default)
    {
        if (session is IAsyncPhotoTextureSession)
        {
            throw new InvalidOperationException("A queued texture session is registered with RegisterAllAsync.");
        }

        // Without an asynchronous session every await below completes synchronously: nothing blocks on a pending task.
        return RegisterAllAsync(textures, ct).GetAwaiter().GetResult();
    }

    /// <summary>Registers the photo onto every texture, awaiting each match of a queued session.</summary>
    /// <param name="textures">The model's textures.</param>
    /// <param name="ct">Cancels the matching and the wait for a runner.</param>
    /// <returns>One registration per texture, in their order.</returns>
    public async Task<List<FacetRegistration>> RegisterAllAsync(IReadOnlyList<RegistrationTexture> textures, CancellationToken ct = default)
    {
        var reuse = direct is { } d && d.Count == textures.Count && d.Select(r => r.FacetId).SequenceEqual(textures.Select(t => t.Frame.FacetId));
        var results = new List<FacetRegistration>();
        foreach (var texture in textures)
        {
            ct.ThrowIfCancellationRequested();
            results.Add(reuse ? direct![results.Count] : await DirectAsync(texture, ct));
        }

        for (var i = 0; i < textures.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            if (!RegistrationAttempts.IsStrong(results[i]) && await AnchoredAsync(textures[i], ct) is { } anchored && RegistrationAttempts.Better(anchored, results[i]))
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
                if (!results[i].Accepted && await SeededAsync(textures, results, i, tried, ct) is { } seeded)
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
    private async Task<FacetRegistration> DirectAsync(RegistrationTexture texture, CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        var best = await MatchAsync(texture, null, PhotoTextureAttempt.Default, ct);
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
            var r = await MatchAsync(texture, null, attempt, ct);
            attempts++;
            best = RegistrationAttempts.Better(r, best) ? r : best;
        }

        Log(best, attempts > 1 ? $" (best of {attempts} attempts)" : null, watch.Elapsed);
        return best;
    }

    /// <summary>The texture matched around the view its anchors predict, or null when it has too few.</summary>
    private async Task<FacetRegistration?> AnchoredAsync(RegistrationTexture texture, CancellationToken ct)
    {
        if (anchors is not { Count: > 0 } || AnchorSeed.Fit(anchors, texture.Frame, session.Width, session.Height) is not { } seed)
        {
            return null;
        }

        return await RegisterAsync(texture, seed.Seed, $" (seeded from {seed.Inliers} placed holds)", ct);
    }

    /// <summary>The target matched around its view predicted from the best untried accepted anchor, or null.</summary>
    private async Task<FacetRegistration?> SeededAsync(
        IReadOnlyList<RegistrationTexture> textures, List<FacetRegistration> results, int target, HashSet<(int Target, int Anchor)> tried, CancellationToken ct)
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
                return await RegisterAsync(textures[target], seed, $" (predicted from facet {results[j].FacetId})", ct);
            }
        }

        return null;
    }

    /// <summary>One seeded photo × texture match, logged.</summary>
    private async Task<FacetRegistration> RegisterAsync(RegistrationTexture texture, double[] seed, string seededBy, CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        var r = await MatchAsync(texture, seed, PhotoTextureAttempt.Default, ct);
        Log(r, seededBy, watch.Elapsed);
        return r;
    }

    /// <summary>One photo × texture match; a matcher failure on one texture only costs that facet.</summary>
    private async Task<FacetRegistration> MatchAsync(RegistrationTexture texture, double[]? seed, PhotoTextureAttempt attempt, CancellationToken ct)
    {
        try
        {
            var mmPerPx = (texture.Frame.MmPerPxA + texture.Frame.MmPerPxB) / 2;
            var match = session is IAsyncPhotoTextureSession queued
                ? await queued.MatchAsync(texture.Image, texture.Mask, mmPerPx, seed, attempt, ct)
                : session.Match(texture.Image, texture.Mask, mmPerPx, seed, attempt);
            return PhotoTextureRegistration.Register(match, session.Width, session.Height, texture.Frame, texture.Extent);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException and not ImageRecognitionUnavailableException)
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
