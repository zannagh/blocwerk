// <copyright file="HoldLinkSuggestionsHint.razor.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Globalization;
using Blocwerk.Core.Abstractions;
using Blocwerk.Core.HoldLinks;
using Blocwerk.Core.Services;
using Blocwerk.Web.Endpoints;
using Microsoft.AspNetCore.Components;

namespace Blocwerk.Web.Components.Shared;

/// <summary>
/// Code-behind of the cross-panel link hint: counts and lists through <see cref="IHoldLinkSuggestionService"/>, which
/// answers 0 for anyone but a wall admin. Crops are the panel photos themselves, scaled and shifted in CSS.
/// </summary>
public partial class HoldLinkSuggestionsHint
{
    // How much of the photo width a thumbnail shows, in hold radii.
    private const double RadiiPerThumb = 6.0;

    private Guid loadedWallId;
    private int count;
    private bool open;
    private bool busy;
    private bool dismissed;
    private string? failure;
    private List<HoldLinkSuggestionView>? rows;

    /// <summary>The wall.</summary>
    [Parameter]
    public Guid WallId { get; set; }

    /// <summary>Set when the page is viewed through a share link: nothing is shown there.</summary>
    [Parameter]
    public string? ShareToken { get; set; }

    /// <summary>Raised after a pair was linked, so the page can update its own link count.</summary>
    [Parameter]
    public EventCallback OnLinked { get; set; }

    [Inject]
    private IHoldLinkSuggestionService Suggestions { get; set; } = default!;

    [Inject]
    private IKioskContext KioskContext { get; set; } = default!;

    [Inject]
    private ILogger<HoldLinkSuggestionsHint> Logger { get; set; } = default!;

    private bool Hidden => KioskContext.IsKiosk || !string.IsNullOrEmpty(ShareToken);

    /// <summary>The hint line ("2 holds seem to appear on two photos without a link.").</summary>
    internal static string HintText(int count) =>
        count == 1
            ? "1 hold seems to appear on two photos without a link."
            : $"{count.ToString(CultureInfo.InvariantCulture)} holds seem to appear on two photos without a link.";

    /// <summary>The photo scaled so <see cref="RadiiPerThumb"/> radii span the thumbnail, with the hold dead centre.</summary>
    internal static string ImgStyle(HoldLinkSuggestionSide h)
    {
        var span = Math.Clamp(h.Radius * RadiiPerThumb, 0.03, 1.0);
        return $"width:{F(100 / span)}%;transform:translate(-{F(h.X * 100)}%,-{F(h.Y * 100)}%)";
    }

    // After render, not in OnParametersSetAsync: never during prerendering (the count would run twice per page load),
    // and again when the retained wall page moves to another wall.
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (WallId == loadedWallId || Hidden)
        {
            return;
        }

        loadedWallId = WallId;
        (count, open, rows, dismissed) = (0, false, null, false);
        try
        {
            count = await Suggestions.CountPendingAsync(WallId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.LogWarning(ex, "Could not count the hold link suggestions of wall {WallId}", WallId);
            count = 0;
        }

        if (count > 0)
        {
            StateHasChanged();
        }
    }

    private static string F(double v) => v.ToString("0.####", CultureInfo.InvariantCulture);

    private string PhotoUrl(Guid panelId) => ResponsiveImage.Initial($"/api/walls/{WallId}/panels/{panelId}/photo");

    private async Task ToggleAsync()
    {
        open = !open;
        if (open && rows is null)
        {
            await LoadRowsAsync();
        }
    }

    private async Task LoadRowsAsync()
    {
        await RunAsync(async () => rows = [.. await Suggestions.ListAsync(WallId)]);
        count = rows?.Count ?? count;
    }

    private Task LinkAsync(HoldLinkSuggestionView s) =>
        RunAsync(async () =>
        {
            await Suggestions.LinkAsync(WallId, s.A.HoldId, s.B.HoldId);
            Drop(s);
            await OnLinked.InvokeAsync();
        });

    private Task RejectAsync(HoldLinkSuggestionView s) =>
        RunAsync(async () =>
        {
            await Suggestions.RejectAsync(WallId, s.A.HoldId, s.B.HoldId);
            Drop(s);
        });

    private void Drop(HoldLinkSuggestionView s)
    {
        rows?.Remove(s);
        count = rows?.Count ?? Math.Max(0, count - 1);
    }

    private async Task RunAsync(Func<Task> action)
    {
        busy = true;
        failure = null;
        try
        {
            await action();
        }
        catch (Exception ex) when (ex is UserFacingException or KioskRestrictedException)
        {
            failure = ex.Message;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.LogWarning(ex, "Hold link suggestion action on wall {WallId} failed", WallId);
            failure = UserFacingException.GenericMessage;
        }
        finally
        {
            busy = false;
        }
    }
}
