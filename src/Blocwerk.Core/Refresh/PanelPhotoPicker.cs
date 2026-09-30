// <copyright file="PanelPhotoPicker.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Services;

namespace Blocwerk.Core.Refresh;

/// <summary>A live panel to find a new photo for.</summary>
public sealed record PanelPhotoTarget(int Col, int Row, byte[] Photo, Guid? PanelId = null);

/// <summary>An uploaded photo to sort, with its sharpness score from the upload (null when unknown).</summary>
public sealed record PhotoToSort(Guid PhotoId, double? Sharpness);

/// <summary>
/// Sorts a visit's photos to the wall's panels: every photo is feature-matched against every current panel photo
/// (<see cref="IImageAlignmentService"/>, the matcher the update already uses to line old and new photos up),
/// scored by <see cref="PanelPhotoScore"/>, and each panel gets its best photo (a photo serves one panel only).
/// </summary>
public sealed class PanelPhotoPicker(IImageAlignmentService alignment)
{
    /// <summary>Long edge the photos are shrunk to before matching (the matcher works at about this size anyway).</summary>
    public const int MatchWidth = 1000;

    private const int KeptCandidates = 4;

    private static int Parallelism => Math.Clamp(Environment.ProcessorCount / 2, 1, 4);

    /// <summary>Scores every photo against every panel and picks one photo per panel.</summary>
    public async Task<IReadOnlyList<PanelPick>> PickAsync(
        IReadOnlyList<PanelPhotoTarget> panels,
        IReadOnlyList<PhotoToSort> photos,
        Func<Guid, CancellationToken, Task<byte[]?>> readPhoto,
        IProgress<int>? progress,
        CancellationToken ct)
    {
        var small = panels.Select(p => Shrink(p.Photo)).ToList();
        var maxSharpness = photos.Select(p => p.Sharpness ?? 0).DefaultIfEmpty(0).Max();
        var scores = panels.Select(_ => new List<PanelPhotoCandidate>()).ToList();
        var done = 0;
        var parallel = new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = Parallelism };
        await Parallel.ForEachAsync(photos, parallel, async (photo, token) =>
        {
            var bytes = await readPhoto(photo.PhotoId, token);
            if (bytes is not null)
            {
                var sharp = maxSharpness > 0 ? (photo.Sharpness ?? 0) / maxSharpness : 1;
                await ScorePhotoAsync(photo.PhotoId, Shrink(bytes), sharp, small, scores);
            }

            progress?.Report(Interlocked.Increment(ref done));
        });

        return Assign(panels, scores);
    }

    /// <summary>
    /// One photo per panel, best pairs first; a panel whose best free photo is not a convincing match keeps its
    /// current photo (<see cref="PanelPickConfidence.None"/>).
    /// </summary>
    public static IReadOnlyList<PanelPick> Assign(
        IReadOnlyList<PanelPhotoTarget> panels, IReadOnlyList<List<PanelPhotoCandidate>> scores)
    {
        var chosen = new Dictionary<int, PanelPhotoCandidate>();
        var used = new HashSet<Guid>();
        var pairs = scores
            .SelectMany((list, panel) => list.Select(c => (Panel: panel, Candidate: c)))
            .OrderByDescending(p => p.Candidate.Score);
        foreach (var (panel, candidate) in pairs)
        {
            if (chosen.ContainsKey(panel) || used.Contains(candidate.PhotoId) || Rate(candidate) == PanelPickConfidence.None)
            {
                continue;
            }

            chosen[panel] = candidate;
            used.Add(candidate.PhotoId);
        }

        return panels.Select((p, i) =>
        {
            var ranked = scores[i].OrderByDescending(c => c.Score).Take(KeptCandidates).ToList();
            return chosen.TryGetValue(i, out var best)
                ? new PanelPick(p.Col, p.Row, best.PhotoId, Rate(best), ranked, p.PanelId)
                : new PanelPick(p.Col, p.Row, null, PanelPickConfidence.None, ranked, p.PanelId);
        }).ToList();
    }

    /// <summary>High: shows nearly all of the panel from about the same angle; Medium: at least half of it.</summary>
    public static PanelPickConfidence Rate(PanelPhotoCandidate candidate)
    {
        if (candidate is { Coverage: >= 0.8, Frontal: >= 0.5, Score: >= 0.55 })
        {
            return PanelPickConfidence.High;
        }

        return candidate is { Coverage: >= 0.5, Score: >= 0.3 } ? PanelPickConfidence.Medium : PanelPickConfidence.None;
    }

    private async Task ScorePhotoAsync(
        Guid photoId, byte[] photo, double sharpness, IReadOnlyList<byte[]> panels, IReadOnlyList<List<PanelPhotoCandidate>> scores)
    {
        for (var i = 0; i < panels.Count; i++)
        {
            // Maps the panel photo (normalized) into the uploaded photo (normalized).
            var h = await alignment.AlignNormalizedAsync(photo, panels[i]);
            if (h is null)
            {
                continue;
            }

            var candidate = PanelPhotoScore.Score(photoId, h, sharpness);
            if (candidate.Score > 0)
            {
                lock (scores[i])
                {
                    scores[i].Add(candidate);
                }
            }
        }
    }

    private static byte[] Shrink(byte[] photo) => ImageRendition.Render(photo, MatchWidth)?.Bytes ?? photo;
}
