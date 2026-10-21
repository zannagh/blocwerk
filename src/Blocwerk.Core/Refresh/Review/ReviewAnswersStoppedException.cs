// <copyright file="ReviewAnswersStoppedException.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Services;

namespace Blocwerk.Core.Refresh.Review;

/// <summary>
/// One answer of several sent to the review was refused: the ones before it are saved, it and the ones after it are not.
/// Says how many were saved and which item was refused, so a page or a script can resume after it.
/// </summary>
public class ReviewAnswersStoppedException : UserFacingException
{
    public ReviewAnswersStoppedException(int answered, string failedKey, UserFacingException reason)
        : base(reason.Message, reason)
    {
        Answered = answered;
        FailedKey = failedKey;
    }

    /// <summary>How many answers were saved, in order, before the refused one.</summary>
    public int Answered { get; }

    /// <summary>The item whose answer was refused.</summary>
    public string FailedKey { get; }
}
