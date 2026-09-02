using Blocwerk.Web.Components.Shared;

namespace Blocwerk.Core.Tests;

/// <summary>
/// Pins the read-back sequencing behind BoulderDetail's offline-queue refresh. The bug this covers
/// was a comment that reached the server but never appeared: the reloads ran under one try/catch,
/// so a failure in an earlier, unrelated read (the favourite/rating lookup) skipped the comment
/// reload entirely and the page stayed stale until the next navigation.
/// </summary>
public class ReloadSequenceTests
{
    [Fact]
    public async Task RunAsync_RunsEveryStep_WhenAnEarlierStepThrows()
    {
        var ran = new List<string>();

        var failure = await ReloadSequence.RunAsync(
            () => throw new InvalidOperationException("favourites are down"),
            () =>
            {
                ran.Add("activity");
                return Task.CompletedTask;
            },
            () =>
            {
                ran.Add("comments");
                return Task.CompletedTask;
            });

        Assert.Equal(["activity", "comments"], ran);
        Assert.Equal("favourites are down", failure);
    }

    [Fact]
    public async Task RunAsync_ReportsTheFirstFailure_AndKeepsGoing()
    {
        var ran = 0;

        var failure = await ReloadSequence.RunAsync(
            () => throw new InvalidOperationException("first"),
            () => throw new InvalidOperationException("second"),
            () =>
            {
                ran++;
                return Task.CompletedTask;
            });

        Assert.Equal(1, ran);
        Assert.Equal("first", failure);
    }

    [Fact]
    public async Task RunAsync_SkipsNullSteps_SoAComponentThatIsNotRenderedYetIsNotAFailure()
    {
        var ran = 0;

        var failure = await ReloadSequence.RunAsync(
            null,
            () =>
            {
                ran++;
                return Task.CompletedTask;
            },
            null);

        Assert.Equal(1, ran);
        Assert.Null(failure);
    }

    [Fact]
    public async Task RunAsync_DoesNotReportAnExpiredSession()
    {
        var ran = 0;

        var failure = await ReloadSequence.RunAsync(
            () => throw new UnauthorizedAccessException(),
            () =>
            {
                ran++;
                return Task.CompletedTask;
            });

        Assert.Equal(1, ran);
        Assert.Null(failure);
    }

    [Fact]
    public async Task RunAsync_ReturnsNull_WhenEveryStepSucceeds()
    {
        var failure = await ReloadSequence.RunAsync(
            () => Task.CompletedTask,
            () => Task.CompletedTask);

        Assert.Null(failure);
    }
}
