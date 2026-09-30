// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Entities;

namespace Blocwerk.Core.Capture;

/// <summary>
/// The texture half of a capture row: a finished capture's <c>Error</c> holds the texture reason first and the
/// photo-real reason after it (<see cref="WallCaptureProcessor.EndWithoutSplat"/>); while the row is in
/// <see cref="WallCaptureStatus.Splatting"/> it holds only the texture reason. A texture re-render
/// (<see cref="WallCaptureService.RerenderTexturesAsync"/>) is marked on <see cref="WallCapture.TexturesJobId"/>.
/// </summary>
internal static class CaptureTextureOutcome
{
    /// <summary>Prefix of <see cref="WallCapture.TexturesJobId"/> while the textures are rendered again (the job id follows once submitted).</summary>
    public const string RerenderMark = "rerender:";

    public static bool IsRerendering(string? texturesJobId) =>
        texturesJobId?.StartsWith(RerenderMark, StringComparison.Ordinal) == true;

    /// <summary>The submitted re-render job, or null when it is still to be submitted (or none runs).</summary>
    public static string? RerenderJobId(string? texturesJobId) =>
        IsRerendering(texturesJobId) && texturesJobId!.Length > RerenderMark.Length ? texturesJobId[RerenderMark.Length..] : null;

    /// <summary>The photo-real half of a finished capture's error, or null.</summary>
    public static string? SplatPart(string? error)
    {
        var i = error?.IndexOf(WallCaptureService.SplatErrorStart, StringComparison.Ordinal) ?? -1;
        return i < 0 ? null : error![i..];
    }

    /// <summary>Puts a new texture outcome on the row (null: textures made), keeping its photo-real outcome.</summary>
    public static void Apply(WallCapture c, string? textureError)
    {
        if (c.Status == WallCaptureStatus.Splatting)
        {
            // The photo-real stage completes from this (see WallCaptureProcessor.CompleteAsync).
            c.Error = Clip(textureError);
            return;
        }

        var splatError = SplatPart(c.Error);
        c.Error = Clip(textureError is null || splatError is null ? textureError ?? splatError : $"{textureError} {splatError}");
        if (textureError is not null)
        {
            c.Status = WallCaptureStatus.SucceededWithoutTextures;
            c.Stage = "Done (without textures)";
        }
        else if (c.Status == WallCaptureStatus.SucceededWithoutTextures)
        {
            c.Status = splatError is null ? WallCaptureStatus.Succeeded : WallCaptureStatus.SucceededWithoutSplat;
            c.Stage = splatError is null ? "Done" : "Done (without the photo-real view)";
        }
    }

    private static string? Clip(string? error) => error is { Length: > 2048 } ? error[..2048] : error;
}
