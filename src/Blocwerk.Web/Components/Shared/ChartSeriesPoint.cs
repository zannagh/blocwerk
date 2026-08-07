namespace Blocwerk.Web.Components.Shared;

/// <summary>
/// One point of a <see cref="ProgressionChart"/> series. <see cref="Value"/> is null for a gap
/// (a bucket with no data — the line breaks there). <see cref="TooltipLabel"/> is the fully
/// formatted string shown on drag (e.g. "13.07.2026 — 6670 / 6C"); <see cref="AxisLabel"/> is the
/// short X-axis tick (e.g. "13.07").
/// </summary>
public record ChartSeriesPoint(double? Value, string TooltipLabel, string AxisLabel);
