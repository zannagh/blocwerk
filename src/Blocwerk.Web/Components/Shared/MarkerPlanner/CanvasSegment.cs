// <copyright file="CanvasSegment.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Web.Components.Shared.MarkerPlanner;

/// <summary>One surface on the canvas.</summary>
/// <param name="Index">Segment index.</param>
/// <param name="Points">SVG polygon points.</param>
/// <param name="LabelX">Label anchor x (SVG frame).</param>
/// <param name="LabelY">Label anchor y (SVG frame).</param>
/// <param name="Title">"#index name".</param>
/// <param name="Detail">Size and angles.</param>
public sealed record CanvasSegment(int Index, string Points, double LabelX, double LabelY, string Title, string Detail);
