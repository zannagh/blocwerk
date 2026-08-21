namespace Blocwerk.Core.Services;

/// <summary>
/// A stored blob (clip or poster frame) together with the content type to serve it as.
/// </summary>
public record BetaVideoContent(byte[] Data, string ContentType);
