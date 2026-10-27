// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Geometry.Footprints;

namespace Blocwerk.Core.Runners;

/// <summary>A panel photo's registered camera in the wall-geometry world, and how well its matched points fit it.</summary>
/// <param name="Camera">The camera (RADIAL k1, k2), its image named by the panel id.</param>
/// <param name="PhotoRevision">The panel photo's revision it was registered from.</param>
/// <param name="Observations">Matched points with a 3D point.</param>
/// <param name="RmsPx">Their reprojection RMS, px.</param>
/// <param name="MedianPx">Their median reprojection error, px.</param>
public sealed record PanelPose(SolvedCamera Camera, int PhotoRevision, int Observations, double RmsPx, double MedianPx);
