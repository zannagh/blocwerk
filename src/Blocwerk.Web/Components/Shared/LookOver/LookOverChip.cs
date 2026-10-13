// <copyright file="LookOverChip.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Web.Components.Shared.LookOver;

/// <summary>One filter of the look-over's chip row, with how many findings it would ask about.</summary>
/// <param name="Filter">The filter the chip sets.</param>
/// <param name="Label">Its short name.</param>
/// <param name="Open">The open findings in it.</param>
public sealed record LookOverChip(LookOverFilter Filter, string Label, int Open);
