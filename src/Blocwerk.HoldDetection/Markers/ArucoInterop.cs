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
    /// OpenCvSharp lines (4.13 additionally groups threshold copies by its default MinGroupDistance).
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
        using var detector = new ArucoDetector(dictionary, parameters, new RefineParameters());
        detector.DetectMarkers(gray, out corners, out ids, out rejected);
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
}
