namespace Blocwerk.Core.Services;

/// <summary>
/// The only two things about a hold that decide whether a set of panels can draw it: the panel it
/// belongs to (null for a hold that was never stamped with one — virtual holds, and every hold
/// created before panels existed) and the generation the hold row itself lives at.
/// </summary>
/// <param name="PanelId">The hold's own panel, or null when it carries none.</param>
/// <param name="Generation">The wall generation this hold row was created at.</param>
public readonly record struct HoldPlacement(Guid? PanelId, int Generation);
