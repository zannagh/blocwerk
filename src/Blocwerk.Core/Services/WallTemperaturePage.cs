using Blocwerk.Core.Entities;

namespace Blocwerk.Core.Services;

/// <summary>
/// A capped slice of a wall's raw temperature series, oldest first.
/// </summary>
/// <param name="Readings">The readings, at most as many as the caller asked for.</param>
/// <param name="Truncated">
/// True when the requested window held more readings than the cap allowed, in which case
/// <paramref name="Readings"/> holds the most recent ones. A caller that must see everything
/// narrows the window or reads the bucketed aggregate instead.
/// </param>
public sealed record WallTemperaturePage(
    IReadOnlyList<WallTemperatureReading> Readings,
    bool Truncated);
