// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Web.Controllers;

/// <summary>One beta clip of a boulder for the companion app: what the web carousel shows, plus where to play it.</summary>
/// <param name="Id">The clip.</param>
/// <param name="UploadedByUserId">Who uploaded it.</param>
/// <param name="UploaderName">Their display name.</param>
/// <param name="CreatedAt">When it was uploaded.</param>
/// <param name="ContentType">The stored rendition's type.</param>
/// <param name="SizeBytes">The stored rendition's size.</param>
/// <param name="EncodingStatus">Pending, Processing, Ready or Failed; a Pending or Processing clip does not play yet.</param>
/// <param name="ThumbnailSha">The poster frame's sha (also readable as <c>/api/walls/{w}/a/{sha}</c>), or null.</param>
/// <param name="VideoUrl">The progressive file, with range requests: <c>/api/v1/beta-videos/{id}</c>.</param>
/// <param name="HlsUrl">The adaptive ladder's master playlist, or null when the clip has none (play <see cref="VideoUrl"/>).</param>
/// <param name="ThumbnailUrl">The poster frame, or null when there is none.</param>
/// <param name="Mine">True for the caller's own clip, which they may delete.</param>
public sealed record DeviceBetaVideoResponse(
    Guid Id,
    Guid UploadedByUserId,
    string UploaderName,
    DateTimeOffset CreatedAt,
    string ContentType,
    long SizeBytes,
    string EncodingStatus,
    string? ThumbnailSha,
    string VideoUrl,
    string? HlsUrl,
    string? ThumbnailUrl,
    bool Mine);
