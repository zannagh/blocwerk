// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Web.Endpoints;

/// <summary>
/// What <see cref="BetaVideoMultipart.ReadAsync"/> read off a beta upload: the clip in a temp file the caller now owns,
/// or the reason it was refused (and the temp file already gone).
/// </summary>
/// <param name="TempPath">The clip's temp file; the caller commits or deletes it.</param>
/// <param name="ContentType">The clip part's declared type.</param>
/// <param name="FileName">The clip's file name, as sent.</param>
/// <param name="Thumbnail">The poster frame, at most <see cref="BetaVideoMultipart.MaxThumbnailBytes"/>, or null.</param>
/// <param name="Error">Why the upload was refused (a 400), or null.</param>
internal sealed record BetaVideoParts(string? TempPath, string? ContentType, string? FileName, byte[]? Thumbnail, string? Error)
{
    public static BetaVideoParts Refused(string error) => new(null, null, null, null, error);
}
