// <copyright file="CanvasMarker.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.MarkerPlanning;

namespace Blocwerk.Web.Components.Shared.MarkerPlanner;

/// <summary>One marker on the canvas.</summary>
/// <param name="Id">Marker id.</param>
/// <param name="Segment">The surface it sits on.</param>
/// <param name="Points">SVG polygon points of its square.</param>
/// <param name="CentreX">Centre x (SVG frame).</param>
/// <param name="CentreY">Centre y (SVG frame).</param>
/// <param name="SizeMm">Printed side.</param>
/// <param name="Role">Corner or filler.</param>
/// <param name="EstimatedPx">Expected on-photo side.</param>
/// <param name="MeetsTarget">True when <paramref name="EstimatedPx"/> reaches the role's target.</param>
public sealed record CanvasMarker(
    int Id, int Segment, string Points, double CentreX, double CentreY, double SizeMm, MarkerRole Role, double EstimatedPx, bool MeetsTarget);
