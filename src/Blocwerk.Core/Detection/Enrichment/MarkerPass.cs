// <copyright file="MarkerPass.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.MarkerPlanning;

namespace Blocwerk.Core.Detection.Enrichment;

/// <summary>What the enrichment's marker detection found, with the plan it detected against; planned afterwards.</summary>
/// <param name="Layout">The wall's current marker layout (or the legacy defaults).</param>
/// <param name="Candidates">Every saved revision of the wall's marker plan.</param>
/// <param name="Detection">The markers on the photo.</param>
internal sealed record MarkerPass(WallMarkerLayout Layout, IReadOnlyList<RevisionCandidate> Candidates, MarkerDetectionResult Detection);
