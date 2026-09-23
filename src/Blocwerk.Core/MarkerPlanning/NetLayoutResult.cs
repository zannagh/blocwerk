// <copyright file="NetLayoutResult.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.MarkerPlanning;

/// <summary>The unfolded net plus what went wrong laying it out.</summary>
/// <param name="Net">The net (only segments reachable from the root).</param>
/// <param name="Issues">Attachment, root and overlap problems.</param>
/// <param name="Transforms">Segment index → its frame's placement in the net.</param>
public sealed record NetLayoutResult(
    NetGeometry Net,
    IReadOnlyList<PlanIssue> Issues,
    IReadOnlyDictionary<int, NetTransform> Transforms);
