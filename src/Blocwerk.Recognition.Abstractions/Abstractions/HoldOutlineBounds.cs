namespace Blocwerk.Core.Abstractions;

/// <summary>An axis-aligned box in normalized image coordinates.</summary>
/// <param name="X">Left edge as a fraction of the image width.</param>
/// <param name="Y">Top edge as a fraction of the image height.</param>
/// <param name="Width">Width as a fraction of the image width.</param>
/// <param name="Height">Height as a fraction of the image height.</param>
public readonly record struct HoldOutlineBounds(double X, double Y, double Width, double Height);
