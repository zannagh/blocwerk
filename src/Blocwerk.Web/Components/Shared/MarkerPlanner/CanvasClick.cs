// <copyright file="CanvasClick.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Web.Components.Shared.MarkerPlanner;

/// <summary>A pointer report from the net canvas.</summary>
/// <param name="Target">The surface index (-1 for none) for a click, or the marker id for a drop.</param>
/// <param name="NetX">Net-space x, mm.</param>
/// <param name="NetY">Net-space y, mm (up).</param>
public readonly record struct CanvasClick(int Target, double NetX, double NetY);
