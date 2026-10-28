// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Client.Views;

/// <summary>One photo the wall view can show: a live panel, or the single photo of a wall without panels.</summary>
/// <param name="Id">The panel, or null for the single photo (its holds carry no panel).</param>
/// <param name="Col">The grid column.</param>
/// <param name="Row">The grid row.</param>
/// <param name="Sha">The photo's sha, or null.</param>
/// <param name="Outdated">True when the panel lags the wall's generation (a sibling was updated ahead of it).</param>
public sealed record PanelView(Guid? Id, int Col, int Row, string? Sha, bool Outdated);
