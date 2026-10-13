// <copyright file="HoldStackPlan.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Entities;

namespace Blocwerk.Core.HoldStacks;

/// <summary>One planned stack: a hold and the hold it would sit on, with how well the two fit the rule.</summary>
/// <param name="Upper">The hold on top.</param>
/// <param name="Base">The hold it sits on.</param>
/// <param name="Inside">The share of the upper hold's area inside the base's outline, 0..1.</param>
/// <param name="AreaRatio">The base's area over the upper hold's.</param>
public sealed record HoldStackPlan(Hold Upper, Hold Base, double Inside, double AreaRatio);
