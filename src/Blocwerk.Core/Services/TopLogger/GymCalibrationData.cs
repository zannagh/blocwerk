namespace Blocwerk.Core.Services.TopLogger;

/// <summary>
/// A gym's calibration prepared for runtime derivation: its (grade, base points) entries sorted
/// ascending by points, plus the flash bonus. Built once per gym during a sync and reused across ticks.
/// </summary>
/// <param name="Sorted">The (grade, base points) entries ascending by points.</param>
/// <param name="FlashBonus">Points added on top of the base grade for a flashed ascent (0 when none).</param>
internal sealed record GymCalibrationData(IReadOnlyList<(string Grade, int Points)> Sorted, int FlashBonus);
