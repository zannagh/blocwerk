// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Text.Json.Serialization;

namespace Blocwerk.Core.Capture.Scan;

/// <summary>
/// The wall's plane as the app measured it, in ARKit world (metres): a point on it and its normal. The normal may come
/// with any non-zero length; <see cref="UnitNormal"/> is it normalised. The app writes this instead of a wall box.
/// </summary>
/// <param name="Point">A point on the plane, <c>[x, y, z]</c>.</param>
/// <param name="Normal">The plane's normal, <c>[x, y, z]</c>, not zero.</param>
public sealed record ScanReferencePlane(double[]? Point, double[]? Normal)
{
    /// <summary>The normal scaled to length 1; null when it is missing, not three finite numbers or of zero length.</summary>
    [JsonIgnore]
    public double[]? UnitNormal
    {
        get
        {
            if (Normal is not { Length: 3 } n || n.Any(v => !double.IsFinite(v)))
            {
                return null;
            }

            var length = Math.Sqrt((n[0] * n[0]) + (n[1] * n[1]) + (n[2] * n[2]));
            return length > 1e-9 && double.IsFinite(length) ? [n[0] / length, n[1] / length, n[2] / length] : null;
        }
    }
}
