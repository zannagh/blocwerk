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
    /// How recent the last real user input must be for an editor to hold the gate. Ten minutes covers somebody
    /// thinking over a wall without touching it; a sleeping laptop or an abandoned tab produces no input at all.
    /// </summary>
    public TimeSpan PresenceWindow { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>How long a hidden, frozen or unloaded editor page still holds the gate (a quick app switch).</summary>
    public TimeSpan HiddenGrace { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// The longest one editing lease may hold the gate, measured from when it started, however busy its user is.
    /// </summary>
    public TimeSpan MaxHold { get; set; } = TimeSpan.FromHours(3);
}
