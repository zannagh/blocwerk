// <copyright file="WallUpdateResume.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Enums;

namespace Blocwerk.Web.Components.Shared;

/// <summary>
/// Where the wall-update wizard reopens for a persisted resume cursor, and what its Resume button says.
/// Pure, so the mapping an API-driven update relies on can be tested without rendering the wizard.
/// </summary>
public static class WallUpdateResume
{
    /// <summary>The step to resume at; <see cref="WallUpdatePhase.Detected"/> for anything unknown or transient.</summary>
    public static WallUpdatePhase TargetFor(WallUpdatePhase? persisted) => persisted switch
    {
        WallUpdatePhase.Carryover => WallUpdatePhase.Carryover,
        WallUpdatePhase.Neighbours => WallUpdatePhase.Neighbours,
        WallUpdatePhase.Touchup => WallUpdatePhase.Touchup,
        WallUpdatePhase.Shapes => WallUpdatePhase.Shapes,
        WallUpdatePhase.ShapeReview => WallUpdatePhase.ShapeReview,
        WallUpdatePhase.Confirm => WallUpdatePhase.Confirm,
        _ => WallUpdatePhase.Detected,
    };

    /// <summary>What the Resume button promises.</summary>
    public static string LabelFor(WallUpdatePhase target) => target switch
    {
        WallUpdatePhase.Carryover => "Resume at the carryover review",
        WallUpdatePhase.Neighbours => "Resume at the overlap review",
        WallUpdatePhase.Touchup => "Resume at the final touch-up",
        WallUpdatePhase.Shapes => "Resume at the hold shapes",
        WallUpdatePhase.ShapeReview => "Resume at the shape review",
        WallUpdatePhase.Confirm => "Resume at the confirmation",
        _ => "Resume at the detected holds",
    };
}
