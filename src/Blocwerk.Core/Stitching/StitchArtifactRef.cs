namespace Blocwerk.Core.Stitching;

/// <summary>
/// A downloadable full-resolution artifact plus its pixel dimensions. Fetch the bytes with
/// <c>GET /jobs/{jobId}/artifacts/{artifact}</c>.
/// </summary>
public sealed record StitchArtifactRef(string Artifact, int Width, int Height);
