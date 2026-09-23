// <copyright file="BigWallUpdate.Shapes.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Enums;

namespace Blocwerk.Web.Components.Shared;

/// <summary>
/// The wizard's optional shape step and its review, between the final touch-up and the confirm step.
/// Both components talk to the shape service themselves; the wizard only moves the phase (and the
/// persisted resume cursor) between them.
/// </summary>
public partial class BigWallUpdate
{
    private ShapeRecognitionStep? _shapeStep;
    private ShapeReviewStep? _shapeReview;

    private Task OnShapesReview() => GoToPhaseAsync(WallUpdatePhase.ShapeReview);

    // Skipping the recognition has already been recorded on the session by the step itself.
    private Task OnShapesSkipped() => GoToPhaseAsync(WallUpdatePhase.Confirm);

    private Task OnShapeReviewContinue() => GoToPhaseAsync(WallUpdatePhase.Confirm);

    private Task OnShapeReviewBack() => GoToPhaseAsync(WallUpdatePhase.Shapes);
}
