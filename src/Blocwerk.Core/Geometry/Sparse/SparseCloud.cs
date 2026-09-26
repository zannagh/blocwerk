// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.Geometry.Sparse;

/// <summary>
/// A capture's COLMAP sparse model reduced to what the volume and protrusion measurements need: the 3D points (in the
/// reconstruction's own frame and units) with their reprojection error and track length, and the camera centres of the
/// capture photos by stem (<c>p01</c>…), which tie the frame to a geometry model's cameras
/// (<see cref="SparseWorldAlignment"/>). No pixels, no colours, no file names beyond the stems.
/// </summary>
/// <param name="PhotoCentres">Camera centre per photo stem, reconstruction frame.</param>
/// <param name="Xyz">x, y, z per point (3 floats each), reconstruction frame.</param>
/// <param name="Error">Mean reprojection error per point, px.</param>
/// <param name="Track">Number of images seeing each point.</param>
public sealed record SparseCloud(
    IReadOnlyDictionary<string, double[]> PhotoCentres,
    float[] Xyz,
    float[] Error,
    ushort[] Track)
{
    /// <summary>Number of points.</summary>
    public int Count => Error.Length;
}
