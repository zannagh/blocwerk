using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Detection.Outlines;
using Blocwerk.Core.Services;
using Blocwerk.Web.Endpoints;
using Microsoft.AspNetCore.Components;

namespace Blocwerk.Web.Components.Shared;

/// <summary>
/// Code-behind for the "Possible duplicate holds" wall-admin card: loads the review list in pages of
/// <see cref="PageSize"/> and merges, dismisses or undoes through <see cref="IHoldDuplicateService"/>.
/// </summary>
public partial class WallDuplicateHolds
{
    private const int PageSize = 20;

    private Guid loadedWallId;
    private bool open;
    private bool busy;
    private int shown = PageSize;
    private string? message;
    private string? failure;
    private HoldDuplicatePage? page;

    /// <summary>The wall being administered.</summary>
    [Parameter]
    public Guid WallId { get; set; }

    /// <summary>Set when the page is viewed through a share link: the card is never offered there.</summary>
    [Parameter]
    public string? ShareToken { get; set; }

    [Inject]
    private IHoldDuplicateService Duplicates { get; set; } = default!;

    [Inject]
    private IKioskContext KioskContext { get; set; } = default!;

    [Inject]
    private ILogger<WallDuplicateHolds> Logger { get; set; } = default!;

    private bool Hidden => KioskContext.IsKiosk || !string.IsNullOrEmpty(ShareToken);

    /// <summary>Why a pair was flagged, in plain words.</summary>
    /// <param name="kind">The kind.</param>
    /// <returns>The sentence.</returns>
    internal static string Reason(HoldDuplicateKind kind) => kind switch
    {
        HoldDuplicateKind.InsideHandPlaced => "An automatic detection sits inside a hand-placed hold.",
        HoldDuplicateKind.NearDuplicateAutomatic => "Two automatic detections cover almost the same spot.",
        _ => "Two hand-placed holds cover almost the same spot.",
    };

    /// <summary>The summary of the open suggestions by kind.</summary>
    /// <param name="p">The page.</param>
    /// <returns>The sentence.</returns>
    internal static string Summary(HoldDuplicatePage p) => p.Total == 0
        ? "No possible duplicates found."
        : $"{PlainCopy.Plural(p.Total, "possible duplicate")}: {p.ByKind[HoldDuplicateKind.InsideHandPlaced]} detection(s) inside a hand-placed hold, "
          + $"{p.ByKind[HoldDuplicateKind.NearDuplicateAutomatic]} pair(s) of automatic detections, {p.ByKind[HoldDuplicateKind.HandPlacedPair]} pair(s) of hand-placed holds.";

    /// <summary>Which boulders a hold is on, in plain words.</summary>
    /// <param name="side">The hold.</param>
    /// <returns>The line.</returns>
    internal static string BoulderText(HoldDuplicateHoldInfo side) => side.BoulderCount == 0
        ? "Not used by any boulder."
        : $"Used by {PlainCopy.Plural(side.BoulderCount, "boulder")}: {string.Join(", ", side.BoulderNames)}{(side.BoulderCount > side.BoulderNames.Count ? ", ..." : string.Empty)}";

    /// <summary>What a side is.</summary>
    /// <param name="side">The hold.</param>
    /// <returns>A short label.</returns>
    internal static string SideKind(HoldDuplicateHoldInfo side) =>
        side.IsVirtual ? "virtual hold (not in the photo)" : side.IsHandMade ? "hand-placed" : "detected automatically";

    protected override void OnParametersSet()
    {
        if (WallId == loadedWallId || Hidden)
        {
            return;
        }

        loadedWallId = WallId;
        open = false;
        page = null;
        shown = PageSize;
        message = null;
        failure = null;
    }

    private Task OpenAsync() => RunAsync(async () =>
    {
        open = true;
        await ReloadAsync();
    });

    private void Close()
    {
        open = false;
        page = null;
        shown = PageSize;
    }

    private Task MoreAsync() => RunAsync(async () =>
    {
        shown += PageSize;
        await ReloadAsync();
    });

    private Task MergeAsync(HoldDuplicateItem item, HoldMergeMode mode) => RunAsync(async () =>
    {
        var result = await Duplicates.MergeAsync(WallId, item.Left.Id, item.Right.Id, mode);
        message = "Merged into one hold."
            + (result.MovedBoulders > 0 ? $" {PlainCopy.Plural(result.MovedBoulders, "boulder")} moved over." : string.Empty)
            + (result.FlaggedForReview > 0 ? $" {PlainCopy.Plural(result.FlaggedForReview, "boulder")} used both holds and now need a quick check." : string.Empty);
        await ReloadAsync();
    });

    private Task DismissAsync(HoldDuplicateItem item) => RunAsync(async () =>
    {
        await Duplicates.DismissAsync(WallId, item.Left.Id, item.Right.Id);
        message = "Noted. This pair will not be suggested again.";
        await ReloadAsync();
    });

    private Task UndoAsync(Guid batchId) => RunAsync(async () =>
    {
        var result = await Duplicates.RevertAsync(WallId, batchId);
        message = result.Reverted ? "Merge undone." : "Something was edited since the merge, so nothing was undone.";
        await ReloadAsync();
    });

    private async Task ReloadAsync() => page = await Duplicates.ListAsync(WallId, 0, shown);

    private async Task RunAsync(Func<Task> action)
    {
        if (busy)
        {
            return;
        }

        busy = true;
        message = null;
        failure = null;
        try
        {
            await action();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException or KioskRestrictedException)
        {
            Logger.LogWarning(ex, "Duplicate hold review action failed for wall {WallId}", WallId);
            failure = ex.Message;
        }
        finally
        {
            busy = false;
        }
    }

    private string CropUrl(Guid holdId, Guid otherId) => WallDuplicateHoldCropEndpoint.Url(WallId, holdId, otherId);
}
