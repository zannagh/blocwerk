// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Services;

namespace Blocwerk.Core.Capture.Scan;

/// <summary>A refused scan request, with the HTTP status it answers with and, for a bad manifest, every problem found.</summary>
public sealed class ScanUploadException(int statusCode, string message, IReadOnlyList<string>? problems = null)
    : UserFacingException(message)
{
    public const int BadRequest = 400;

    public const int NotFound = 404;

    public const int Conflict = 409;

    public const int TooLarge = 413;

    /// <summary>A file whose bytes or hash are not the manifest's.</summary>
    public const int Unprocessable = 422;

    public int StatusCode { get; } = statusCode;

    public IReadOnlyList<string> Problems { get; } = problems ?? [];
}
