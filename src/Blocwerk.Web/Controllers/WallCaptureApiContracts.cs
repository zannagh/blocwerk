// <copyright file="WallCaptureApiContracts.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Text.Json.Serialization;
using Blocwerk.Core.Capture;
using Blocwerk.Core.Entities;

namespace Blocwerk.Web.Controllers;

/// <summary>
/// Starts a capture draft. Everything is optional: without <see cref="Segments"/> the suggested declarations are used
/// (from the marker plan, the wall's previous capture or its segments), without <see cref="LevelPairs"/> the
/// suggested pairs, and <see cref="Quality"/> defaults to High (used only when a photo-real worker is configured).
/// </summary>
/// <param name="Segments">The per-segment declarations (index, name, declaredAngleDeg, verticalReference).</param>
/// <param name="LevelPairs">Marker id pairs known to be level, e.g. [[14, 15]].</param>
/// <param name="Notes">Free text for the capture history.</param>
/// <param name="Quality">The photo-real view's quality profile: Draft, High or Max.</param>
/// <param name="GeometryMode">auto (default), markers or features: forces the marker solve or the feature reconstruction (wall admins only).</param>
public sealed record CaptureStartRequest(
    IReadOnlyList<CaptureSegmentDeclaration>? Segments = null,
    IReadOnlyList<int[]>? LevelPairs = null,
    string? Notes = null,
    [property: JsonConverter(typeof(JsonStringEnumConverter<SplatQuality>))] SplatQuality Quality = SplatQuality.High,
    [property: JsonConverter(typeof(JsonStringEnumConverter<CaptureGeometryOverride>))]
    CaptureGeometryOverride GeometryMode = CaptureGeometryOverride.Auto);

/// <summary>A started capture: poll <c>GET …/captures/{captureId}</c> for its status.</summary>
/// <param name="CaptureId">The capture.</param>
/// <param name="Warnings">What will happen that the caller may not expect (e.g. a named segment without an angle is merged).</param>
public sealed record CaptureStartResponse(Guid CaptureId, IReadOnlyList<string> Warnings);

/// <summary>Why a draft could not be started (nothing was changed).</summary>
/// <param name="Problems">The problems, in plain words.</param>
public sealed record CaptureStartProblems(IReadOnlyList<string> Problems);

/// <summary>The declarations the server would use, with the warnings they raise.</summary>
/// <param name="Declarations">Segments and level pairs.</param>
/// <param name="Warnings">E.g. "Segment 5 (“Cave”) has no angle, so its markers will be merged …".</param>
public sealed record CaptureDeclarationsResponse(CaptureDeclarations Declarations, IReadOnlyList<string> Warnings);

/// <summary>One file of a multipart photo upload.</summary>
/// <param name="FileName">The file name as sent.</param>
/// <param name="Photo">The stored photo (markers found, size, focal length), or null when it was refused.</param>
/// <param name="Error">Why it was refused, or null.</param>
public sealed record CapturePhotoUploadItem(string? FileName, CapturePhotoResult? Photo, string? Error);

/// <summary>The outcome of a multipart photo upload: one item per file part, in upload order.</summary>
/// <param name="Stored">Photos stored.</param>
/// <param name="Refused">Photos refused (see each item's error).</param>
/// <param name="Items">Per file.</param>
public sealed record CapturePhotoUploadResponse(int Stored, int Refused, IReadOnlyList<CapturePhotoUploadItem> Items);

/// <summary>Scope of an outline upgrade request.</summary>
/// <param name="IncludeManual">Also outline circles drawn by hand (default: auto-detected ones only).</param>
public sealed record OutlineUpgradeRequest(bool IncludeManual = false);

/// <summary>A surface of the active model, named by its facet id (the model corrections).</summary>
/// <param name="FacetId">The facet id, e.g. "1b".</param>
public sealed record GeometryFacetRequest(string FacetId);
