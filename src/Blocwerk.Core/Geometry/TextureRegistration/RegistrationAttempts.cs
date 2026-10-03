// <copyright file="RegistrationAttempts.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Abstractions;

namespace Blocwerk.Core.Geometry.TextureRegistration;

/// <summary>
/// When a photo × facet registration counts as strong, how two are compared, and the deterministic attempts a weak
/// one is retried with. The coarse match is chaotic in its inputs (the copy wall's right panel photo on its main
/// wall: 1214 inliers, and 131 with the same texture file whose bounds were scaled by 1.0027), so a weak result is
/// matched again with other RANSAC samples and each coarse scale on its own, and the best result is kept. The seeds
/// derive from the photo and the facet only: the same inputs always give the same registration.
/// </summary>
public static class RegistrationAttempts
{
    /// <summary>Inliers a strong registration has (below: weak, tried again and seeded from linked holds).</summary>
    public const int StrongInliers = 300;

    /// <summary>Coverage of the facet in view a strong registration has.</summary>
    public const double StrongCoverage = 0.30;

    /// <summary>RANSAC seeds tried per coarse scale (the first keeps the matches' own order).</summary>
    public const int Seeds = 5;

    /// <summary>How long the attempts on one photo × facet may take in total, after the first.</summary>
    public static readonly TimeSpan DefaultBudget = TimeSpan.FromSeconds(15);

    /// <summary>A factor on the coarse texture resolution per seed, so the coarse images differ as well.</summary>
    private static readonly double[] Jitters = [1, 0.97, 1.03, 0.94, 1.06];

    /// <summary>Whether a registration is accepted with at least <see cref="StrongInliers"/> inliers over <see cref="StrongCoverage"/>.</summary>
    /// <param name="r">The registration.</param>
    /// <returns>True when strong.</returns>
    public static bool IsStrong(FacetRegistration r) => r.Accepted && r.Inliers >= StrongInliers && r.Coverage >= StrongCoverage;

    /// <summary>
    /// Whether retrying can help: the facet was found (a fit with at least half the inliers acceptance needs) but not
    /// strongly. A facet the coarse search does not find at all is usually out of view; the view predicted from a
    /// neighbouring facet or from anchors finds it when it is not, at a fraction of the cost.
    /// </summary>
    /// <param name="r">The registration so far.</param>
    /// <returns>True when another attempt is worth its time.</returns>
    public static bool WorthRetrying(FacetRegistration r) => !IsStrong(r) && r.Inliers >= PhotoTextureRegistration.MinInliers / 2;

    /// <summary>The comparison score: inliers × coverage.</summary>
    /// <param name="r">The registration.</param>
    /// <returns>The score.</returns>
    public static double Score(FacetRegistration r) => r.Inliers * r.Coverage;

    /// <summary>Whether <paramref name="candidate"/> beats <paramref name="current"/>: accepted first, then by <see cref="Score"/>.</summary>
    /// <param name="candidate">The new registration.</param>
    /// <param name="current">The one so far.</param>
    /// <returns>True when the candidate is better.</returns>
    public static bool Better(FacetRegistration candidate, FacetRegistration current) =>
        candidate.Accepted != current.Accepted ? candidate.Accepted : Score(candidate) > Score(current);

    /// <summary>
    /// The retries after <see cref="PhotoTextureAttempt.Default"/>: per seed, each coarse scale on its own. Seed 0 keeps the
    /// found order and the configured scales; the others are fixed per (photo, facet).
    /// </summary>
    /// <param name="photo">The photo's stable label (e.g. "c1 r0").</param>
    /// <param name="facetId">The facet.</param>
    /// <param name="coarsePasses">How many coarse scales the matcher has.</param>
    /// <returns>The attempts, in order.</returns>
    public static IEnumerable<PhotoTextureAttempt> Retries(string photo, string facetId, int coarsePasses = 2)
    {
        for (var k = 0; k < Seeds; k++)
        {
            var seed = k == 0 ? 0 : StableSeed($"{photo}|{facetId}|{k}");
            for (var pass = 0; pass < coarsePasses; pass++)
            {
                yield return new PhotoTextureAttempt(seed, pass, Jitters[k % Jitters.Length]);
            }
        }
    }

    /// <summary>A non-zero seed from a text (FNV-1a), the same in every process.</summary>
    internal static int StableSeed(string text)
    {
        unchecked
        {
            var h = 2166136261;
            foreach (var c in text)
            {
                h = (h ^ c) * 16777619;
            }

            var seed = (int)(h & 0x7fffffff);
            return seed == 0 ? 1 : seed;
        }
    }
}
