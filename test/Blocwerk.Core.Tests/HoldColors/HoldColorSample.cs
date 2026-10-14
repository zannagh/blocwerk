// <copyright file="HoldColorSample.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Holds.Colors;

namespace Blocwerk.Core.Tests.HoldColors;

/// <summary>One hold with a colour somebody set, and the pixels the colour estimate reads for it.</summary>
/// <param name="HoldId">The hold.</param>
/// <param name="Stored">The colour stored on it.</param>
/// <param name="PanelId">The panel whose photo it was read on; empty for the wall photo.</param>
/// <param name="Own">The pixels inside its outline.</param>
/// <param name="Surroundings">The pixels around it.</param>
internal sealed record HoldColorSample(Guid HoldId, string Stored, Guid PanelId, List<OklabColor> Own, List<OklabColor> Surroundings);
