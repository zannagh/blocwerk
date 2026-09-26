using System.Text.Json.Serialization;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Geometry;
using Blocwerk.Core.MarkerPlanning;

namespace Blocwerk.Core.Capture;

/// <summary>What the admin declares about one marker segment (a plan segment, or <c>markerId / 6</c>) for a solve.</summary>
public sealed record CaptureSegmentDeclaration(
    [property: JsonPropertyName("index")] int Index,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("declaredAngleDeg")] double? DeclaredAngleDeg,
    [property: JsonPropertyName("verticalReference")] bool VerticalReference);

/// <summary>All declarations of a capture, stored on <see cref="WallCapture.DeclarationsJson"/>.</summary>
public sealed record CaptureDeclarations(
    [property: JsonPropertyName("segments")] IReadOnlyList<CaptureSegmentDeclaration> Segments,
    [property: JsonPropertyName("levelPairs")] IReadOnlyList<int[]> LevelPairs)
{
    public static CaptureDeclarations Empty { get; } = new([], []);
}

/// <summary>One validated, refined marker of a capture photo (pixel corners TL, TR, BR, BL), as stored.</summary>
public sealed record CaptureMarker(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("corners")] double[][] Corners,
    [property: JsonPropertyName("synthetic")] bool Synthetic,
    [property: JsonPropertyName("sidePx")] double SidePx);

/// <summary>What an upload produced, for the upload list and the declarations table.</summary>
public sealed record CapturePhotoResult(
    Guid PhotoId,
    int Index,
    string? FileName,
    int Width,
    int Height,
    double? Focal35mm,
    IReadOnlyList<int> MarkerIds,
    IReadOnlyList<string> Warnings);

/// <summary>
/// A capture row for the status panel and history (no photo bytes, no JSON payloads). <c>FollowUp</c> is what the
/// post-capture chain did in plain words ("856 holds placed on the 3D model, …"); <c>FollowUpNote</c> what it could
/// not do, or a quiet note such as a skipped photo-real view. Both null until the chain ran. <c>WallId</c> is the
/// capture's wall (from its row). <c>ModelChecks</c> is what the solver said about the model this capture made
/// (measured segment angles, warnings, ignored detections), empty without a model. <c>PhotoRealPending</c>: the
/// capture is done, but its photo-real view waits for (or trains on) a 3D runner; a quiet one-liner, else null.
/// </summary>
public sealed record WallCaptureSummary(
    Guid Id,
    DateTimeOffset CreatedAt,
    WallCaptureStatus Status,
    double Progress,
    string? Stage,
    string? Error,
    string? Notes,
    int PhotoCount,
    Guid? GeometryModelId,
    DateTimeOffset? CompletedAt,
    MarkerPlacementCheck? PlacementCheck = null,
    SplatQuality? SplatQuality = null,
    string? FollowUp = null,
    string? FollowUpNote = null,
    Guid WallId = default,
    IReadOnlyList<WallGeometryModelCheck>? ModelChecks = null,
    string? PhotoRealPending = null)
{
    public bool IsRunning => Status is WallCaptureStatus.Queued or WallCaptureStatus.Detecting
        or WallCaptureStatus.Solving or WallCaptureStatus.Texturing or WallCaptureStatus.Splatting;
}

/// <summary>A draft capture with its photos (and the marker plan it runs with), so an open upload can be resumed.</summary>
public sealed record WallCaptureDraft(
    Guid CaptureId,
    string? Notes,
    IReadOnlyList<CapturePhotoResult> Photos,
    CapturePlanInfo? Plan = null,
    CaptureVideoInfo? Video = null,
    CaptureScaleReference? ScaleReference = null);

/// <summary>The draft's walk-along video (photo-real view only): as uploaded, before its frames are taken.</summary>
public sealed record CaptureVideoInfo(string? FileName, long SizeBytes, double? DurationSeconds);

/// <summary>The marker plan a draft runs with; absent for the legacy <c>segment*6+role</c> ids.</summary>
/// <param name="Uploaded">True when the plan was uploaded with the photos, false when it is the wall's saved plan.</param>
/// <param name="Segments">Planned surfaces.</param>
/// <param name="Markers">Planned markers.</param>
/// <param name="MaxMarkerId">Highest planned id (the limit for level pairs).</param>
/// <param name="IdsBySegment">The planned ids per segment index, for the declarations table.</param>
/// <param name="Revision">The wall plan revision it is (null when unknown).</param>
public sealed record CapturePlanInfo(
    bool Uploaded, int Segments, int Markers, int MaxMarkerId, IReadOnlyDictionary<int, IReadOnlyList<int>> IdsBySegment, int? Revision = null);

/// <summary>The outcome of uploading (or removing) a draft's marker plan.</summary>
/// <param name="Accepted">True when the draft now runs with it.</param>
/// <param name="Errors">Why the plan was refused (parse or validation errors).</param>
/// <param name="Notes">What happened, e.g. that it became the wall's plan.</param>
public sealed record CapturePlanResult(bool Accepted, IReadOnlyList<string> Errors, IReadOnlyList<string> Notes);

/// <summary>
/// One texture of the wall's ACTIVE geometry model, for the 3D view: the facet it belongs to, the
/// URL to fetch it from (already authorized for the caller's way in, share token included) and the
/// facet-plane millimetres it spans. <c>MaskUrl</c> is its coverage mask, authorized the same way; null
/// when the texture has none.
/// </summary>
public sealed record WallGeometryTextureInfo(
    Guid ModelId,
    string FacetId,
    string Url,
    double AMin,
    double AMax,
    double BMin,
    double BMax,
    int WidthPx,
    int HeightPx,
    string? MaskUrl = null);

/// <summary>
/// One stored capture photo, read back for reuse as a panel photo: the wall it belongs to (from the
/// capture row, never the caller) and its metadata-stripped bytes exactly as stored.
/// </summary>
public sealed record CapturePhotoFile(Guid WallId, Guid PhotoId, string? FileName, byte[] Bytes, string ContentType);
