namespace Blocwerk.Core.Stitching;

/// <summary>The <c>202</c> body of <c>POST /jobs</c>.</summary>
public sealed record StitchJobCreationResult(string JobId, string Status);
