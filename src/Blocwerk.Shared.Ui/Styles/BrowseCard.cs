// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Shared.Ui.Styles;

/// <summary>One card of the boulder browser.</summary>
/// <param name="Key">What it stands for (<c>all</c>, <c>holds</c>, <c>crimp</c>...), for tests and styling.</param>
/// <param name="Label">Its name.</param>
/// <param name="Icon">Its icon's SVG content.</param>
/// <param name="Count">How many families it leads to; a card with none is greyed out and leads nowhere.</param>
/// <param name="Href">Where a tap goes.</param>
/// <param name="Wide">True for a card across the whole row.</param>
public sealed record BrowseCard(string Key, string Label, string Icon, int Count, string Href, bool Wide = false);
