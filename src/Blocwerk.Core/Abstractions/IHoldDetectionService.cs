namespace Blocwerk.Core.Abstractions;

public interface IHoldDetectionService
{
    Task<List<DetectedHold>> DetectHoldsAsync(byte[] imageData, HoldDetectionParameters? parameters = null);
}

public record DetectedHold(double X, double Y, double Radius, string? Color, double Confidence);

public record HoldDetectionParameters(
    int MinArea = 400,
    int MaxArea = 50000,
    int BlurSize = 5,
    int SaturationThreshold = 40);
