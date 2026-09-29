// <copyright file="OpenCvHoldPresenceProbe.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Abstractions;
using OpenCvSharp;

namespace Blocwerk.HoldDetection.Matching;

/// <summary>
/// <see cref="IHoldPresenceProbe"/> by template matching: the new photo's patch around the spot, resized to
/// the old photo's scale, searched in a window of the old photo around the aligned spot (so a few
/// pixels of alignment error do not matter). Both photos decode once per call, grey, in the RAW frame. Near a
/// photo edge the patch shrinks and the window is clipped to the photo, so a spot at the frame is still judged.
/// </summary>
public sealed class OpenCvHoldPresenceProbe : IHoldPresenceProbe
{
    private const int PatchRadius = 56;
    private const int SearchSlack = 70;
    private const int MinPatchRadius = 24;

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
        var radius = Math.Min(PatchRadius, Math.Min(Math.Min(x, y), Math.Min(newGrey.Cols - 1 - x, newGrey.Rows - 1 - y)));
        if (radius < MinPatchRadius || q.Scale is <= 0.2 or >= 5)
        {
            return null;
        }

        using var source = new Mat(newGrey, new Rect(x - radius, y - radius, 2 * radius, 2 * radius));
        using var template = new Mat();
        Cv2.Resize(source, template, new Size(0, 0), q.Scale, q.Scale);
        var half = (template.Rows / 2) + SearchSlack;
        var window = Clip((int)q.OldX - half, (int)q.OldY - half, (int)q.OldX + half, (int)q.OldY + half, oldGrey);
        if (template.Rows >= window.Height || template.Cols >= window.Width)
        {
            return null;
        }

        using var searched = new Mat(oldGrey, window);
        using var result = new Mat();
        Cv2.MatchTemplate(searched, template, result, TemplateMatchModes.CCoeffNormed);
        Cv2.MinMaxLoc(result, out _, out double max);
        return double.IsFinite(max) ? max : null;
    }

    private static Rect Clip(int x0, int y0, int x1, int y1, Mat m)
    {
        x0 = Math.Max(x0, 0);
        y0 = Math.Max(y0, 0);
        x1 = Math.Min(x1, m.Cols - 1);
        y1 = Math.Min(y1, m.Rows - 1);
        return new Rect(x0, y0, Math.Max(x1 - x0, 0), Math.Max(y1 - y0, 0));
    }
}
