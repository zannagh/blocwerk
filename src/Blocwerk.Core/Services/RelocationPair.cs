// <copyright file="RelocationPair.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.Services;

/// <summary>A relocation pair worth suggesting.</summary>
/// <param name="OldHoldId">The disappeared old hold.</param>
/// <param name="NewHoldId">The appeared staged hold.</param>
/// <param name="Score">Fingerprint similarity, 0..1.</param>
/// <param name="Margin">Lead over the best competing pair.</param>
/// <param name="Metric">Whether millimetre sizes took part in the score.</param>
public sealed record RelocationPair(Guid OldHoldId, Guid NewHoldId, double Score, double Margin, bool Metric);
