using OpenCvSharp;
using OpenCvSharp.Aruco;

namespace Blocwerk.HoldDetection.Markers;

/// <summary>
/// The only place that touches the ArUco API, because it differs between the two OpenCvSharp
/// lines the app builds against. macOS (4.8.0.20230708, symbol <c>OPENCVSHARP_LEGACY_ARUCO</c>)
/// has the free function <c>CvAruco.DetectMarkers</c> + <c>PredefinedDictionaryName</c>; the
/// Linux containers (4.13) only have the <c>ArucoDetector</c> class + <c>PredefinedDictionaryType</c>.
/// Both native runtimes export the matching entry points (verified with nm).
/// </summary>
internal static class ArucoInterop
{
    /// <summary>Creates the DICT_4X4_50 dictionary. Caller disposes.</summary>
    public static Dictionary CreateDict4X4With50()
    {
#if OPENCVSHARP_LEGACY_ARUCO
        return CvAruco.GetPredefinedDictionary(PredefinedDictionaryName.Dict4X4_50);
#else
        return CvAruco.GetPredefinedDictionary(PredefinedDictionaryType.Dict4X4_50);
#endif
    }

    /// <summary>
    /// Detector settings proven on the real wall photos (61 → 82 detections vs. defaults).
    /// Deliberately no CLAHE upstream: it cut detections 82 → 57 (glare, not exposure, limits).
    /// <c>MinMarkerDistanceRate = 0</c> switches off OpenCV's too-close filter, which keeps only the
    /// biggest of nested quads: with a thin white cut-out on a darker wall that is the paper's outline,
    /// not the black square (the marker was lost or its corners landed on the paper edge / screw heads).
    /// <see cref="NestedMarkerCollapser"/> takes over the de-duplication. Same parameters on both
    /// OpenCvSharp lines; on 4.13 <see cref="Detect"/> runs them one threshold window at a time.
    /// </summary>
    public static DetectorParameters CreateTunedParameters()
    {
        var p = new DetectorParameters
        {
            CornerRefinementMethod = CornerRefineMethod.Subpix,
            AdaptiveThreshWinSizeMin = 3,
            AdaptiveThreshWinSizeMax = 53,
            AdaptiveThreshWinSizeStep = 5,
            MinMarkerPerimeterRate = 0.01,
            PerspectiveRemovePixelPerCell = 8,
            MinMarkerDistanceRate = 0,
        };
        return p;
    }

    /// <summary>Runs marker detection on a grayscale image.</summary>
    public static void Detect(
        Mat gray,
        Dictionary dictionary,
        DetectorParameters parameters,
        out Point2f[][] corners,
        out int[] ids,
        out Point2f[][] rejected)
    {
#if OPENCVSHARP_LEGACY_ARUCO
        CvAruco.DetectMarkers(gray, dictionary, out corners, out ids, parameters, out rejected);
#else
        DetectPerThresholdWindow(gray, dictionary, parameters, out corners, out ids, out rejected);
#endif
    }

    /// <summary>
    /// Renders one DICT_4X4_50 marker as an 8-bit image of <paramref name="sidePx"/> pixels.
    /// The black square (incl. its 1-cell border) fills the whole image; add a quiet zone yourself.
    /// </summary>
    public static Mat GenerateMarker(int id, int sidePx)
    {
        using var dictionary = CreateDict4X4With50();
        var marker = new Mat();
        dictionary.GenerateImageMarker(id, sidePx, marker, 1);
        return marker;
    }

#if !OPENCVSHARP_LEGACY_ARUCO
    /// <summary>
    /// 4.8 parity on the 4.13 line. With <c>MinMarkerDistanceRate = 0</c>, 4.8 decodes every quad of
    /// every threshold window on its own. 4.13 instead nests all quads into a containment tree and
    /// decodes it innermost-first, skipping the ancestors of a decoded quad; its loop counter counts
    /// those ancestors twice, so with the many nested window copies it stops before the deeper levels
    /// and whole markers vanish (a real crop lost id 8, a print test ids 5 and 8). One window per
    /// pass keeps each tree a few levels deep; <see cref="NestedMarkerCollapser"/> merges the copies,
    /// as it does for 4.8's output.
    /// </summary>
    private static void DetectPerThresholdWindow(
        Mat gray,
        Dictionary dictionary,
        DetectorParameters parameters,
        out Point2f[][] corners,
        out int[] ids,
        out Point2f[][] rejected)
    {
        var allCorners = new List<Point2f[]>();
        var allIds = new List<int>();
        var allRejected = new List<Point2f[]>();
        var single = parameters;

        // 4.13 caps the sub-pixel window at 0.3 module (2 px on a 42 px marker); 4.8 always uses
        // CornerRefinementWinSize. A huge relative size makes that the effective size again.
        single.RelativeCornerRefinmentWinSize = 1000f;
        for (var win = parameters.AdaptiveThreshWinSizeMin; win <= parameters.AdaptiveThreshWinSizeMax; win += parameters.AdaptiveThreshWinSizeStep)
        {
            single.AdaptiveThreshWinSizeMin = win;
            single.AdaptiveThreshWinSizeMax = win;
            using var detector = new ArucoDetector(dictionary, single, new RefineParameters());
            detector.DetectMarkers(gray, out var windowCorners, out var windowIds, out var windowRejected);
            allCorners.AddRange(windowCorners);
            allIds.AddRange(windowIds);
            allRejected.AddRange(windowRejected);
        }

        corners = [.. allCorners];
        ids = [.. allIds];
        rejected = [.. allRejected];
    }
#endif
}
