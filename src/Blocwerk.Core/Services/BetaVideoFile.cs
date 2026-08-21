namespace Blocwerk.Core.Services;

/// <summary>
/// How to serve a clip: a physical path to stream from disk (current storage), or the raw bytes
/// (legacy in-database clips). Exactly one of <see cref="PhysicalPath"/> / <see cref="Bytes"/> is set.
/// </summary>
public record BetaVideoFile(string? PhysicalPath, byte[]? Bytes, string ContentType, string? FileName);
