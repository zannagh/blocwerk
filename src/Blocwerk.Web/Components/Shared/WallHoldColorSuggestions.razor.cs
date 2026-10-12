// <copyright file="WallHoldColorSuggestions.razor.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Services;
using Microsoft.AspNetCore.Components;

namespace Blocwerk.Web.Components.Shared;

/// <summary>
/// Code-behind for the "Suggest hold colours" wall-admin action, through <see cref="IHoldColorProposalService"/>
/// (one change-journal batch per run).
/// </summary>
public partial class WallHoldColorSuggestions
{
    private Guid loadedWallId;
    private bool busy;
    private string? message;
    private string? failure;
    private HoldColorProposalStatus? status;

    /// <summary>The wall being administered.</summary>
    [Parameter]
    public Guid WallId { get; set; }

    /// <summary>Set when the page is viewed through a share link: the action is never offered there.</summary>
    [Parameter]
    public string? ShareToken { get; set; }

    [Inject]
    private IHoldColorProposalService Proposals { get; set; } = default!;

    [Inject]
    private IKioskContext KioskContext { get; set; } = default!;

    [Inject]
    private ILogger<WallHoldColorSuggestions> Logger { get; set; } = default!;

    private bool Hidden => KioskContext.IsKiosk || !string.IsNullOrEmpty(ShareToken);

    /// <summary>The result line: how many holds got a colour and how many stayed without one.</summary>
    /// <param name="report">The run's report.</param>
    /// <returns>The sentence shown under the button.</returns>
    internal static string Describe(HoldColorProposalReport report)
    {
        if (report.Considered == 0)
        {
            return "Every hold already has a colour.";
        }

        var got = report.Proposed + report.FromLinked;
        var linked = report.FromLinked > 0 ? $" ({report.FromLinked} from the same hold on another panel)" : string.Empty;
        return $"{PlainCopy.Plural(got, "hold")} got a colour{linked}. "
            + $"{PlainCopy.Plural(report.LeftUnset, "hold")} stayed without one because the photo does not say clearly.";
    }

    // Route data is loaded here, not in OnInitializedAsync: WallDetail is retained across enhanced navigation.
    protected override async Task OnParametersSetAsync()
    {
        if (WallId == loadedWallId || Hidden)
        {
            return;
        }

        loadedWallId = WallId;
        message = null;
        failure = null;
        await ReloadAsync();
    }

    private static bool IsExpected(Exception ex) =>
        ex is UnauthorizedAccessException or InvalidOperationException or KioskRestrictedException or UserFacingException;

    private async Task ReloadAsync()
    {
        try
        {
            status = await Proposals.GetStatusAsync(WallId);
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            Logger.LogWarning(ex, "Could not load the colour suggestion status of wall {WallId}", WallId);
            status = null;
        }
    }

    private async Task SuggestAsync()
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
            message = Describe(await Proposals.SuggestMissingAsync(WallId));
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            Logger.LogWarning(ex, "Suggesting hold colours failed for wall {WallId}", WallId);
            failure = ex.Message;
        }
        finally
        {
            busy = false;
            await ReloadAsync();
        }
    }
}
