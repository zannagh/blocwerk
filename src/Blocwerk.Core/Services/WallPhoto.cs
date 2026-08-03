namespace Blocwerk.Core.Services;

/// <summary>
/// A wall photo together with its content type, for a specific generation.
/// </summary>
public record WallPhoto(byte[] Photo, string? ContentType);
