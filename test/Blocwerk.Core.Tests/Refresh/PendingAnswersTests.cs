// <copyright file="PendingAnswersTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Enums;
using Blocwerk.Core.Refresh.Review;
using Blocwerk.Web.Components.Shared.Refresh.Review;

namespace Blocwerk.Core.Tests.Refresh;

/// <summary>The review page's unsent answers: the latest answer per item wins, also over one kept for Retry.</summary>
public class PendingAnswersTests
{
    [Fact]
    public void AnAnswerGivenAfterAFailure_ReplacesTheUnsavedOne_AndRetrySendsNoOlderAnswer()
    {
        var pending = new PendingAnswers();
        pending.Add(new("k-a", ReviewAnswer.Gone));
        pending.Add(new("k-b", ReviewAnswer.StillThere));
        pending.Failed(pending.TakeBatch());
        Assert.Equal(2, pending.Unsaved);

        pending.Add(new("k-a", ReviewAnswer.StillThere));
        var sent = pending.TakeBatch();
        var retried = pending.TakeUnsaved();

        Assert.Equal([new ReviewAnswerRequest("k-a", ReviewAnswer.StillThere)], sent);
        Assert.Equal([new ReviewAnswerRequest("k-b", ReviewAnswer.StillThere)], retried);
        Assert.Equal((0, 0), (pending.Waiting, pending.Unsaved));
    }

    [Fact]
    public void AFailedBatch_KeepsOneAnswerPerItem_TheLatest_AndNoneAnsweredAgainMeanwhile()
    {
        var pending = new PendingAnswers();
        pending.Add(new("n-a", ReviewAnswer.KeepNew));
        pending.Add(new("n-a", ReviewAnswer.NotAHold));
        pending.Add(new("n-b", ReviewAnswer.KeepNew));
        var batch = pending.TakeBatch();
        pending.Add(new("n-b", ReviewAnswer.NotAHold));

        pending.Failed(batch);

        Assert.Equal([new ReviewAnswerRequest("n-a", ReviewAnswer.NotAHold)], pending.TakeUnsaved());
        Assert.True(pending.IsWaiting("n-b"));
    }
}
