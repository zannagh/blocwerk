// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Enums;

namespace Blocwerk.Core.Helpers;

/// <summary>A boulder's dominant hold type as the browser files it.</summary>
/// <param name="Type">The hold type.</param>
/// <param name="Inferred">True when it comes from the boulder's holds, false when somebody set it on the boulder.</param>
public readonly record struct EffectiveHoldType(HoldHandType Type, bool Inferred);
