// <copyright file="HoldLinkSuggestionView.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.HoldLinks;

/// <summary>A pending suggestion for the review list.</summary>
/// <param name="A">One hold.</param>
/// <param name="B">The other hold, on another panel photo.</param>
/// <param name="DistanceMm">How far apart they sit in 3D, mm.</param>
public sealed record HoldLinkSuggestionView(HoldLinkSuggestionSide A, HoldLinkSuggestionSide B, double DistanceMm);

/// <summary>One hold of a suggestion, where it is on its panel photo.</summary>
/// <param name="HoldId">The hold.</param>
/// <param name="PanelId">Its panel photo.</param>
/// <param name="PanelName">The panel's name for people ("Right panel").</param>
/// <param name="X">Normalised position across the photo.</param>
/// <param name="Y">Normalised position down the photo.</param>
/// <param name="Radius">Normalised radius on the photo.</param>
/// <param name="Color">Its colour key, null when not set.</param>
public sealed record HoldLinkSuggestionSide(Guid HoldId, Guid PanelId, string PanelName, double X, double Y, double Radius, string? Color);
