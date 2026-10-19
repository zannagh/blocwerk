// <copyright file="RefreshCheckAnswersStoppedException.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Services;

namespace Blocwerk.Core.Refresh;

/// <summary>
/// Thrown when one answer of several was refused: the ones before it are saved, it and the ones after it are not. Says
/// how many were saved and which card was refused, so a script can resume after it.
/// </summary>
public class RefreshCheckAnswersStoppedException : UserFacingException
{
    public RefreshCheckAnswersStoppedException(int answered, Guid failedCheckId, UserFacingException reason)
        : base(reason.Message, reason)
    {
        Answered = answered;
        FailedCheckId = failedCheckId;
    }

    /// <summary>How many answers were saved, in order, before the refused one.</summary>
    public int Answered { get; }

    /// <summary>The card whose answer was refused (nothing from it on was saved).</summary>
    public Guid FailedCheckId { get; }
}
