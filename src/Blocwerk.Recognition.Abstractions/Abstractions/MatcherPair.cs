// <copyright file="MatcherPair.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.Abstractions;

/// <summary>The two photos an overlap pass compares, with the holds detected on each.</summary>
/// <param name="LeftImage">Encoded bytes of the left photo.</param>
/// <param name="LeftHolds">The holds on the left photo.</param>
/// <param name="RightImage">Encoded bytes of the right photo.</param>
/// <param name="RightHolds">The holds on the right photo.</param>
public sealed record MatcherPair(byte[] LeftImage, IReadOnlyList<MatcherHold> LeftHolds, byte[] RightImage, IReadOnlyList<MatcherHold> RightHolds);
