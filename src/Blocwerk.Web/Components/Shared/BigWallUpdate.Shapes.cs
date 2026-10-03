// <copyright file="BigWallUpdate.Shapes.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Microsoft.AspNetCore.Components;

namespace Blocwerk.Web.Components.Shared;

/// <summary>
/// The wizard's optional shape step and its review, between the final touch-up and the confirm step.
/// Both components talk to the shape service themselves; the wizard only moves the phase (and the
/// persisted resume cursor) between them.
/// </summary>
public partial class BigWallUpdate
{
    [Inject]
    private IWallUpdateShapeService ShapeService { get; set; } = default!;

    private ShapeRecognitionStep? _shapeStep;
    private ShapeReviewStep? _shapeReview;

    private Task OnShapesReview() => GoToPhaseAsync(WallUpdatePhase.ShapeReview);

    // Skipping the recognition has already been recorded on the session by the step itself.
    private async Task OnShapesSkipped()
    {
        await GoToPhaseAsync(WallUpdatePhase.Confirm);
        await LoadShapeSummaryAsync();
    }

    /// <summary>
    /// Leaving the review goes through the shape service — the same call the API's review/complete makes —
    /// so the session's cursor moves the same way whoever finishes the step.
    /// </summary>
    private async Task OnShapeReviewContinue()
    {
        try
        {
            await ShapeService.CompleteReviewAsync(WallId, _sessionInfo?.Id);
            _sessionInfo = await Sessions.GetOpenSessionAsync(WallId);
            _phase = WallUpdatePhase.Confirm;
            await LoadShapeSummaryAsync();
        }
        catch (WallUpdateSessionSupersededException)
        {
            MarkSuperseded();
        }
        catch (Exception ex)
        {
            _error = $"Could not finish the shape review: {ex.Message}";
        }
    }

    private Task OnShapeReviewBack() => GoToPhaseAsync(WallUpdatePhase.Shapes);

    /// <summary>The shape step's tally for the confirm summary; null when there is nothing to show.</summary>
    private ShapeRecognitionStatusInfo? _shapeSummary;

    private int ShapeCount(ShapeReviewDecision decision) => _shapeSummary?.Tally.GetValueOrDefault(decision) ?? 0;

    private async Task LoadShapeSummaryAsync()
    {
        try
        {
            _shapeSummary = await ShapeService.GetStatusAsync(WallId);
        }
        catch (Exception)
        {
            // The summary is informational; the promote reads the session itself.
            _shapeSummary = null;
        }
    }
}
