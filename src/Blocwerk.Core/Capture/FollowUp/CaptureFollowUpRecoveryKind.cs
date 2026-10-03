// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.Capture.FollowUp;

/// <summary>Which resumable mark of a <see cref="CaptureFollowUpRecord"/> a startup recovery picks up.</summary>
public enum CaptureFollowUpRecoveryKind
{
    /// <summary><see cref="CaptureFollowUpRecord.Rederive"/>: the follow-ups of an adopted re-solve.</summary>
    Rederive,

    /// <summary><see cref="CaptureFollowUpRecord.RunAgain"/>: the chain after a correction or re-activation.</summary>
    RunAgain,
}
