// <copyright file="ScriptedFollowUpStep.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Capture.FollowUp;

namespace Blocwerk.Core.Tests;

/// <summary>
/// A scripted post-capture step: records every call in a shared log, then does what <see cref="Run"/> says
/// (by default: done, with <see cref="Summary"/>).
/// </summary>
internal sealed class ScriptedFollowUpStep(string key, int order, List<string> log, bool needsPhotoReal = false) : ICaptureFollowUpStep
{
    public string Key => key;

    public int Order => order;

    public string Title => $"Step {key}";

    public bool NeedsPhotoReal => needsPhotoReal;

    public string Summary { get; set; } = string.Empty;

    /// <summary>What the step does; default: done with <see cref="Summary"/>.</summary>
    public Func<CaptureFollowUpContext, CancellationToken, CaptureFollowUpStepResult>? Run { get; set; }

    /// <summary>The contexts it ran with.</summary>
    public List<CaptureFollowUpContext> Calls { get; } = [];

    public Task<CaptureFollowUpStepResult> RunAsync(CaptureFollowUpContext context, CancellationToken ct)
    {
        log.Add(key);
        Calls.Add(context);
        return Task.FromResult(Run?.Invoke(context, ct) ?? CaptureFollowUpStepResult.Done(Summary));
    }
}
