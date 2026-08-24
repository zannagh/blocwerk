namespace Blocwerk.Core.Stitching;

/// <summary>An input photo the sidecar could not use, with the reason it was dropped.</summary>
public sealed record StitchRejectedImage(string Name, string Reason);
