// <copyright file="LookOverHoldNow.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.LookOver;

/// <summary>A hold as it is now, as far as a finding's view needs it.</summary>
/// <param name="Id">The hold.</param>
/// <param name="Name">Its name.</param>
/// <param name="Color">Its colour.</param>
/// <param name="PanelId">The panel photo it is on.</param>
/// <param name="Generation">Its generation.</param>
/// <param name="Placed">It has a position on the 3D model.</param>
/// <param name="Live">
/// It is on one of the wall's live photos at that photo's generation. A row that is not is history (its panel was
/// photographed again): it can be shown, never changed.
/// </param>
internal sealed record LookOverHoldNow(Guid Id, string? Name, string? Color, Guid? PanelId, int Generation, bool Placed, bool Live = false);
