namespace Blocwerk.Core.Stitching;

/// <summary>Sidecar-reported failure detail.</summary>
public sealed record StitchJobError(string Code, string Message);
