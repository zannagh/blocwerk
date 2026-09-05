using Blocwerk.Core.Enums;

namespace Blocwerk.Core.Services;

/// <summary>
/// Everything the carousel needs about one beta video — deliberately without the blob, so
/// listing a boulder's betas never loads the clips themselves.
/// </summary>
public record BetaVideoInfo(
    Guid Id,
    Guid BoulderId,
    Guid UploadedByUserId,
    string UploaderName,
    DateTimeOffset CreatedAt,
    string ContentType,
    long SizeBytes,
    bool HasThumbnail,
    bool HasHls,
    BetaVideoEncodingStatus EncodingStatus);
