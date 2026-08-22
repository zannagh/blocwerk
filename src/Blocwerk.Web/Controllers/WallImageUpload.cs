namespace Blocwerk.Web.Controllers;

/// <summary>
/// Outcome of reading an image off the request: either a committed-to-temp file the caller now
/// owns, or the status code and message the caller has to return verbatim.
/// </summary>
internal sealed record WallImageUpload
{
    private WallImageUpload()
    {
    }

    public int? ErrorStatus { get; private init; }

    public string? ErrorMessage { get; private init; }

    /// <summary>Temp file holding the bytes. The caller must commit or delete it.</summary>
    public string TempPath { get; private init; } = string.Empty;

    public string ContentType { get; private init; } = string.Empty;

    public string Extension { get; private init; } = string.Empty;

    public long SizeBytes { get; private init; }

    public string? Caption { get; private init; }

    public DateTimeOffset? CapturedAt { get; private init; }

    public bool IsSuccess => ErrorStatus is null;

    public static WallImageUpload Failed(int status, string message)
    {
        return new WallImageUpload { ErrorStatus = status, ErrorMessage = message };
    }

    public static WallImageUpload Succeeded(
        string tempPath,
        string contentType,
        string extension,
        long sizeBytes,
        string? caption,
        DateTimeOffset? capturedAt)
    {
        return new WallImageUpload
        {
            TempPath = tempPath,
            ContentType = contentType,
            Extension = extension,
            SizeBytes = sizeBytes,
            Caption = caption,
            CapturedAt = capturedAt,
        };
    }
}
