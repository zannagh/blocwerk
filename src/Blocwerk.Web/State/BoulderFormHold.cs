// <copyright file="BoulderFormHold.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;

namespace Blocwerk.Web.State;

/// <summary>One hold mark in a <see cref="BoulderFormSnapshot"/> (enum values as numbers, as the snapshot writes them).</summary>
public sealed record BoulderFormHold(Guid HoldId, int Type, int Usage)
{
    /// <summary>The mark as the services take it.</summary>
    public BoulderHoldInput ToInput() => new(HoldId, (HoldType)Type, (HoldUsage)Usage);
}
