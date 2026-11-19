// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Enums;

namespace Blocwerk.Core.Helpers;

/// <summary>A boulder's prominent hold types as the browser files it.</summary>
/// <param name="Types">The types, never <see cref="BoulderHoldType.None"/>.</param>
/// <param name="Inferred">True when they come from the boulder's holds (a single type), false when somebody set them on the boulder.</param>
public readonly record struct EffectiveHoldType(BoulderHoldType Types, bool Inferred);
