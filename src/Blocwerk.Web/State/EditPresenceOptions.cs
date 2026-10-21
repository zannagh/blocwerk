// <copyright file="EditPresenceOptions.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Web.State;

/// <summary>
/// When an open editor holds the deploy gate (<c>/health/ready-to-deploy</c>). Bound from the <c>EditPresence</c>
/// configuration section; see <see cref="EditDeployGatePolicy"/>.
/// </summary>
/// <remarks>
/// These narrow the gate only. A lease that stops holding the gate is still a lease: it still keeps background hold
/// writers off the wall (<see cref="EditActivityWallEditPresence"/>) until <see cref="EditActivityPolicy"/> expires it.
/// </remarks>
public sealed class EditPresenceOptions
{
    /// <summary>The configuration section these are bound from.</summary>
    public const string SectionName = "EditPresence";

    /// <summary>
    /// How recent the last real user input must be for an editor to hold the gate. Thirty minutes, because a setter
    /// drafting a boulder on the kiosk tablet walks off to try the moves on the wall between two taps; a sleeping
    /// laptop or an abandoned tab produces no input at all.
    /// </summary>
    public TimeSpan PresenceWindow { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>How long a hidden, frozen or unloaded editor page still holds the gate (a quick app switch).</summary>
    public TimeSpan HiddenGrace { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// The IANA (or Windows) id of the time zone <see cref="CutoffTimeOfDay"/> is read in. An unknown id falls back
    /// to UTC, so a typo can shift the cutoff but never remove it.
    /// </summary>
    public string CutoffTimeZone { get; set; } = "Europe/Berlin";

    /// <summary>
    /// The local time of day after which no editing lease holds the gate any more: a lease lets go at the first
    /// occurrence of this time after it started (see <see cref="EditDeployCutoff"/>). Midnight by default, so a
    /// pending deploy waits at most until the coming midnight.
    /// </summary>
    public TimeSpan CutoffTimeOfDay { get; set; } = TimeSpan.Zero;

    /// <summary>
    /// How long <c>POST /api/v1/maintenance/flush-edits</c> waits for the open editors to save before it answers.
    /// </summary>
    public TimeSpan FlushTimeout { get; set; } = TimeSpan.FromSeconds(30);
}
