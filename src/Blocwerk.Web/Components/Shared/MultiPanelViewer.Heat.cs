// <copyright file="MultiPanelViewer.Heat.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Microsoft.JSInterop;

namespace Blocwerk.Web.Components.Shared;

/// <summary>The usage heat map half of the panel viewer: its state and the canvas paint call.</summary>
public partial class MultiPanelViewer
{
    private bool _heatOn;
    private bool _showCounts;

    // What the canvas was last painted for, so a render that changes nothing does not repaint it.
    private string? _heatPaintedKey;

    /// <summary>The busiest hold on the whole wall, so every panel shares one colour scale.</summary>
    private int HeatMax => UsageCounts is { Count: > 0 } ? UsageCounts.Values.Max() : 0;

    private void ToggleHeat()
    {
        _heatOn = !_heatOn;
        _showCounts = false;
        _heatPaintedKey = null;
    }

    private void ToggleCounts() => _showCounts = !_showCounts;

    private int UsageCount(Guid holdId) =>
        UsageCounts is not null && UsageCounts.TryGetValue(holdId, out var count) ? count : 0;

    private async Task PaintHeatAsync()
    {
        if (!_heatOn || _panel is null || UsageCounts is null)
        {
            _heatPaintedKey = null;
            return;
        }

        var key = $"{_panel.Id}:{_holds.Count}:{HeatMax}";
        if (key == _heatPaintedKey)
        {
            return;
        }

        _heatPaintedKey = key;
        var points = _holds
            .Where(h => UsageCount(h.Id) > 0)
            .Select(h => new { x = h.X, y = h.Y, r = h.Radius, c = UsageCount(h.Id) })
            .ToList();
        await JS.InvokeVoidAsync("bwHeat.render", _viewportRef, points, HeatMax);
    }
}
