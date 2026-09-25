// <copyright file="VideoAdvice.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Globalization;

namespace Blocwerk.Core.Capture.Coverage;

/// <summary>The video lines of the "what to add" list.</summary>
public static class VideoAdvice
{
    /// <summary>Below this share of placed frames the video is called out.</summary>
    public const double MinRegisteredShare = 0.8;

    /// <summary>What the next video should add.</summary>
    /// <param name="video">The video against the recipe.</param>
    /// <returns>The lines.</returns>
    public static IEnumerable<CoverageAdvice> Lines(VideoCoverage video)
    {
        if (!video.HasVideo)
        {
            yield return new CoverageAdvice("video", "Add a walk-along video: the photo-real view fills its gaps from its frames");
        }
        else if (video.FramesRegistered is { } placed && video.FramesExtracted > 0 && placed < video.FramesExtracted * MinRegisteredShare)
        {
            yield return new CoverageAdvice("video", string.Create(
                CultureInfo.InvariantCulture,
                $"Only {placed} of {video.FramesExtracted} video frames could be placed: walk slower and keep the wall in view"));
        }

        var missing = video.Passes.Where(p => !p.Present).ToList();
        var fromVideo = video.PosesFrom == CoveragePoseSource.Video;
        foreach (var pass in missing.Where(p => !IsVolume(p)))
        {
            var text = fromVideo
                ? $"The video has no {pass.Label}"
                : $"Add {Article(pass.Label)} {pass.Label} to the video: no photo was taken like that";
            yield return new CoverageAdvice("video", text);
        }

        var volumes = missing.Where(IsVolume).Select(VolumeNumber).OfType<int>().Order().ToList();
        if (volumes.Count > 0)
        {
            var all = volumes.Count > 1 && volumes.Count == video.Passes.Count(IsVolume);
            var which = all ? "each volume" : volumes.Count == 1 ? CoverageWhere.Volumes(volumes) : $"each of {CoverageWhere.Volumes(volumes)}";
            var them = volumes.Count == 1 ? "it" : "them";
            var text = fromVideo
                ? $"The video has no semicircle under {which}"
                : $"Walk a semicircle under {which} in the video: the photos do not see {them} from below all round";
            yield return new CoverageAdvice("video", text);
        }
    }

    private static string Article(string label) => "aeiou".Contains(label[0], StringComparison.Ordinal) ? "an" : "a";

    private static bool IsVolume(RecipePass p) => p.Key.StartsWith("under-volume-", StringComparison.Ordinal);

    private static int? VolumeNumber(RecipePass p) =>
        int.TryParse(p.Key["under-volume-".Length..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;
}
