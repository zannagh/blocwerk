namespace Blocwerk.Core.Stitching;

/// <summary>
/// One image part of a <c>POST /jobs</c> request. Photos are handheld shots (jpeg/png/heic) and
/// are held as bytes because the sidecar needs them all in a single multipart body.
/// </summary>
public sealed record StitchPhotoUpload(string FileName, string ContentType, byte[] Content);
