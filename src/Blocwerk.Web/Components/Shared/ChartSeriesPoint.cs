namespace Blocwerk.Web.Components.Shared;

/// <summary>
/// One point of a <see cref="ProgressionChart"/> series. <see cref="Value"/> is null for a gap
/// (a bucket with no data — the line breaks there). <see cref="TooltipLabel"/> is the fully
/// formatted string shown on drag (e.g. "13.07.2026 — 6670 / 6C"); <see cref="AxisLabel"/> is the
/// short X-axis tick (e.g. "13.07").
/// </summary>
/// <param name="Value">The plotted value, or null for a gap.</param>
/// <param name="TooltipLabel">The drag read-out text. In a chart with
/// <c>LocalTimeLabels</c> this is the prefix and the point's local time is appended client-side.</param>
/// <param name="AxisLabel">The short X-axis tick text (the pre-JS fallback in local-time mode).</param>
/// <param name="UtcMs">The point's UTC instant as Unix epoch milliseconds. Set only when the chart
/// formats the axis and tooltip time client-side (<c>LocalTimeLabels</c>); null otherwise.</param>
public record ChartSeriesPoint(double? Value, string TooltipLabel, string AxisLabel, long? UtcMs = null);
