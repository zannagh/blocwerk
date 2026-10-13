namespace Blocwerk.Core.Abstractions;

public interface IHoldDetectionService
{
    Task<List<DetectedHold>> DetectHoldsAsync(byte[] imageData, HoldDetectionParameters? parameters = null);
}

/// <summary>One detection on a photo, in fractions of the image.</summary>
/// <param name="X">Centre X as a fraction of the image width.</param>
/// <param name="Y">Centre Y as a fraction of the image height.</param>
/// <param name="Radius">Half the detector box's longer side, as a fraction of the image's longer side.</param>
/// <param name="Color">A colour the detector implies ("white" for a volume), else null.</param>
/// <param name="Confidence">The detector's confidence.</param>
/// <param name="BoxWidth">The detector box width as a fraction of the image width; null when the detector has no box.</param>
/// <param name="BoxHeight">The detector box height as a fraction of the image height; null when the detector has no box.</param>
public record DetectedHold(double X, double Y, double Radius, string? Color, double Confidence, double? BoxWidth = null, double? BoxHeight = null);

public record HoldDetectionParameters(
    int MinArea = 400,
    int MaxArea = 50000,
    int BlurSize = 5,
    int SaturationThreshold = 40);
