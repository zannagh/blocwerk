// <copyright file="PrintedMarker.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.MarkerPlanning;

/// <summary>Where a marker's black square lands in the PDF: its page and its top-left corner (page mm, y down).</summary>
/// <param name="Id">Marker id.</param>
/// <param name="PageIndex">0-based page index.</param>
/// <param name="LeftMm">Left edge of the black square.</param>
/// <param name="TopMm">Top edge of the black square.</param>
/// <param name="SizeMm">Side of the black square.</param>
/// <param name="BorderMm">White border from the black square to the cut line.</param>
public sealed record PrintedMarker(int Id, int PageIndex, double LeftMm, double TopMm, double SizeMm, double BorderMm);
