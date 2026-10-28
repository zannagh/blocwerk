// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Contracts.Documents;

/// <summary>One point of an outline, as an offset from its owner's normalised centre (a hold) or origin (a segment).</summary>
/// <param name="Dx">The horizontal offset.</param>
/// <param name="Dy">The vertical offset.</param>
public sealed record DocumentPoint(double Dx, double Dy);
