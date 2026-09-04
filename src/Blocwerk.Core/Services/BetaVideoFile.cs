using Blocwerk.Core.Enums;

namespace Blocwerk.Core.Services;

/// <summary>
/// How to serve a clip: a physical path to stream from disk (current storage), or the raw bytes
/// (legacy in-database clips). Exactly one of <see cref="PhysicalPath"/> / <see cref="Bytes"/> is set.
/// <see cref="EncodingStatus"/> lets a direct-GET caller withhold a clip that is still
/// <see cref="BetaVideoEncodingStatus.Pending"/>/<see cref="BetaVideoEncodingStatus.Processing"/>
/// (its served file may be mid-swap, or the upload is not yet verified web-safe).
/// </summary>
public record BetaVideoFile(
    string? PhysicalPath,
    byte[]? Bytes,
    string ContentType,
    string? FileName,
    BetaVideoEncodingStatus EncodingStatus);
