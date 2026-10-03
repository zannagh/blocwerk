// <copyright file="CaptureDetection.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.Geometry.Proposals;

/// <summary>One hold detection in one capture photo, in that photo's pixels.</summary>
/// <param name="Photo">The photo's camera name (<c>p01</c>…).</param>
/// <param name="Px">Box centre x, px.</param>
/// <param name="Py">Box centre y, px.</param>
/// <param name="RadiusPx">Half the box's longer side, px.</param>
/// <param name="Confidence">Detector confidence.</param>
public sealed record CaptureDetection(string Photo, double Px, double Py, double RadiusPx, double Confidence);
