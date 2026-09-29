// <copyright file="OpenCvHoldPresenceProbe.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Abstractions;
using OpenCvSharp;

namespace Blocwerk.HoldDetection.Matching;

/// <summary>
/// <see cref="IHoldPresenceProbe"/> by template matching: the new photo's patch around the spot, resized to
/// the old photo's scale, searched in a window of the old photo around the aligned spot (so a few
/// pixels of alignment error do not matter). Both photos decode once per call, grey, in the RAW frame.
/// </summary>
public sealed class OpenCvHoldPresenceProbe : IHoldPresenceProbe
{
    private const int PatchRadius = 56;
    private const int SearchSlack = 70;

    public IReadOnlyList<double?> Score(byte[] oldImage, byte[] newImage, IReadOnlyList<PresenceQuery> queries)
    {
        const ImreadModes Mode = ImreadModes.Grayscale | ImreadModes.IgnoreOrientation;
        using var oldGrey = Cv2.ImDecode(oldImage, Mode);
        using var newGrey = Cv2.ImDecode(newImage, Mode);
        if (oldGrey.Empty() || newGrey.Empty())
        {
            return queries.Select(_ => (double?)null).ToList();
        }

        return queries.Select(q => ScoreOne(oldGrey, newGrey, q)).ToList();
    }

    private static double? ScoreOne(Mat oldGrey, Mat newGrey, PresenceQuery q)
    {
        var x = (int)q.NewX;
        var y = (int)q.NewY;
        var patch = new Rect(x - PatchRadius, y - PatchRadius, 2 * PatchRadius, 2 * PatchRadius);
        if (!Inside(patch, newGrey) || q.Scale is <= 0.2 or >= 5)
        {
            return null;
        }

        using var source = new Mat(newGrey, patch);
        using var template = new Mat();
        Cv2.Resize(source, template, new Size(0, 0), q.Scale, q.Scale);
        var half = (template.Rows / 2) + SearchSlack;
        var window = new Rect((int)q.OldX - half, (int)q.OldY - half, 2 * half, 2 * half);
        if (!Inside(window, oldGrey) || template.Rows >= window.Height || template.Cols >= window.Width)
        {
            return null;
        }

        using var searched = new Mat(oldGrey, window);
        using var result = new Mat();
        Cv2.MatchTemplate(searched, template, result, TemplateMatchModes.CCoeffNormed);
        Cv2.MinMaxLoc(result, out _, out double max);
        return double.IsFinite(max) ? max : null;
    }

    private static bool Inside(Rect r, Mat m) => r.X >= 0 && r.Y >= 0 && r.Right < m.Cols && r.Bottom < m.Rows;
}
