using Blocwerk.Core.Abstractions;
using Blocwerk.HoldDetection.Alignment;
using Serilog;

namespace Blocwerk.HoldDetection;

public sealed class SkiaImageAlignmentService : IImageAlignmentService
{
    public Task<Homography?> AlignAsync(byte[] baseImage, byte[] imageToAlign) =>
        Run(() => ImageAlignment.Estimate(baseImage, imageToAlign));

    public Task<Homography?> AlignNormalizedAsync(byte[] baseImage, byte[] imageToAlign) =>
        Run(() => ImageAlignment.EstimateNormalized(baseImage, imageToAlign));

    private static Task<Homography?> Run(Func<Homography?> estimate)
    {
        return Task.Run(() =>
        {
            try
            {
                var result = estimate();
                if (result == null)
                {
                    Log.Information("[Image Alignment] No reliable alignment found");
                }
                else
                {
                    Log.Information(
                        "[Image Alignment] Aligned with {Inliers} inliers (confidence {Confidence})",
                        result.Inliers,
                        result.Confidence);
                }

                return result;
            }
            catch (Exception ex)
            {
                Log.Warning("[Image Alignment] Failed ({Type}: {Message})", ex.GetType().Name, ex.Message);
                return null;
            }
        });
    }
}
