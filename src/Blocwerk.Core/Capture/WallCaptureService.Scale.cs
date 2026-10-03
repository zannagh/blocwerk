// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Text.Json;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Capture;

/// <summary>"Make sizes exact" on a draft: one measured distance for the feature solve's scale.</summary>
public sealed partial class WallCaptureService
{
    /// <summary>The longest distance a capture may declare, mm (a 100 m wall is no climbing wall).</summary>
    public const double MaxScaleReferenceMm = 100_000;

    private static readonly JsonSerializerOptions ScaleJson = new(JsonSerializerDefaults.Web);

    public async Task SetScaleReferenceAsync(Guid captureId, CaptureScaleReference? reference)
    {
        var (db, userId, capture) = await OpenDraftAsync(captureId);
        await using (db)
        {
            if (reference is not null)
            {
                var photo = await db.WallCapturePhotos.AsNoTracking()
                    .FirstOrDefaultAsync(p => p.CaptureId == captureId && p.Index == reference.PhotoIndex)
                    ?? throw new UserFacingException("That photo is not part of this capture.");
                if (ScaleReferenceProblem(reference, photo.Width, photo.Height) is { } problem)
                {
                    throw new UserFacingException(problem);
                }
            }

            capture.ScaleReferenceJson = reference is null
                ? null
                : JsonSerializer.Serialize(reference with { A = [reference.A[0], reference.A[1]], B = [reference.B[0], reference.B[1]] }, ScaleJson);
            await db.SaveChangesAsync();
            logger.LogInformation(
                "Capture {CaptureId}: measured distance {State} by {UserId}", captureId, reference is null ? "removed" : "set", userId);
        }
    }

    /// <summary>Why a measured distance cannot be used on a photo of <paramref name="width"/>×<paramref name="height"/>, or null.</summary>
    /// <param name="reference">The distance.</param>
    /// <param name="width">The stored photo's width, px.</param>
    /// <param name="height">The stored photo's height, px.</param>
    /// <returns>The problem in words, or null.</returns>
    public static string? ScaleReferenceProblem(CaptureScaleReference reference, int width, int height)
    {
        if (reference.A is not { Length: 2 } || reference.B is not { Length: 2 } || !reference.A.Concat(reference.B).All(double.IsFinite))
        {
            return "Tap two points on the photo.";
        }

        if (!(reference.Mm > 0) || reference.Mm > MaxScaleReferenceMm)
        {
            return "Enter the distance between the two points in millimetres.";
        }

        bool Inside(double[] p) => p[0] >= 0 && p[1] >= 0 && p[0] <= width && p[1] <= height;
        if (!Inside(reference.A) || !Inside(reference.B))
        {
            return "Both points must be on the photo.";
        }

        var px = Math.Sqrt(Math.Pow(reference.A[0] - reference.B[0], 2) + Math.Pow(reference.A[1] - reference.B[1], 2));
        return px < 0.05 * Math.Max(width, height) ? "The two points are too close together on the photo: pick two points farther apart." : null;
    }
}
