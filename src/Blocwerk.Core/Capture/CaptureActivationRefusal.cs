// <copyright file="CaptureActivationRefusal.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Geometry;

namespace Blocwerk.Core.Capture;

/// <summary>
/// The capture's message when its model was stored but not activated. The sanity gate and the registration refuse
/// independently; both reasons are named (The Attic, 2026-10-26: the gate's "5.0 px" hid that the registration had
/// refused the same model for down-weighting 10 of its 49 markers).
/// </summary>
internal static class CaptureActivationRefusal
{
    /// <summary>The message for a model that is not activated.</summary>
    /// <param name="problems">The sanity gate's problems (<see cref="WallGeometrySanityGate.Problems(string)"/>).</param>
    /// <param name="registrationRefusal">The registration's refusal, when it refused and said why.</param>
    public static string Message(IReadOnlyList<string> problems, string? registrationRefusal)
    {
        if (problems.Count > 0)
        {
            return WallGeometrySanityGate.Describe(problems)
                   + (registrationRefusal is null ? string.Empty : " It could not be tied to the active model either: " + registrationRefusal)
                   + " It was saved but NOT activated, so hold positions stay as they are. "
                   + "Check the ignored detections in the solver notes and re-capture the parts they name.";
        }

        return (registrationRefusal is null ? string.Empty : registrationRefusal + " ")
               + "The new 3D model was saved but NOT activated, so hold positions stay as they are. Re-capture with more "
               + "unchanged markers in view, or activate the new model from the model history if its frame change is acceptable.";
    }
}
