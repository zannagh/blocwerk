// <copyright file="SuggestHoldLinksFollowUpStep.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Abstractions;

namespace Blocwerk.Core.Capture.FollowUp;

/// <summary>
/// Step 3b (at the end of the capture, after the holds were placed and put onto their volumes): holds of different
/// panel photos that now sit on the same spot in 3D but are not linked become link suggestions for a wall admin
/// (<see cref="IHoldLinkSuggestionService.RefreshFromPipelineAsync"/>). It only suggests: nothing is linked here.
/// Runs again whenever the volumes step does (a new photo-real view), since volume points move holds.
/// </summary>
public sealed class SuggestHoldLinksFollowUpStep(IHoldLinkSuggestionService suggestions) : ICaptureFollowUpStep
{
    /// <inheritdoc />
    public string Key => "suggest-hold-links";

    /// <inheritdoc />
    public int Order => 260;

    /// <inheritdoc />
    public string Title => "Looking for holds seen on two photos";

    /// <inheritdoc />
    public bool NeedsPhotoReal => true;

    /// <inheritdoc />
    public bool KeptByCorrection => false;

    /// <inheritdoc />
    public async Task<CaptureFollowUpStepResult> RunAsync(CaptureFollowUpContext context, CancellationToken ct)
    {
        var pending = await suggestions.RefreshFromPipelineAsync(context.WallId, ct);
        return pending is null
            ? CaptureFollowUpStepResult.Skipped("the holds could not be compared on the 3D model")
            : CaptureFollowUpStepResult.Done(Describe(pending.Value));
    }

    /// <summary>"2 holds seem to appear on two photos without a link" (empty when there are none).</summary>
    internal static string Describe(int pending) =>
        pending <= 0
            ? string.Empty
            : $"{CaptureFollowUpText.Count(pending, "hold seems", "holds seem")} to appear on two photos without a link";
}
