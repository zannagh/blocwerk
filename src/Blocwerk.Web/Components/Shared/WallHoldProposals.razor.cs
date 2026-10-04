// <copyright file="WallHoldProposals.razor.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Globalization;
using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Microsoft.AspNetCore.Components;

namespace Blocwerk.Web.Components.Shared;

/// <summary>
/// Code-behind of the hold-proposal review (the volumes are <see cref="WallVolumeList"/>): lists through
/// <see cref="IHoldProposalService"/>; a proposal's crop comes as a data URL (a ~20 KB JPEG each).
/// </summary>
public partial class WallHoldProposals
{
    private const int PageSize = 20;
    private const int CropBatch = 6;
    private readonly Dictionary<Guid, string> crops = [];
    private readonly HashSet<Guid> cropFailed = [];
    private int shown = PageSize;
    private bool cropping;
    private readonly Dictionary<Guid, string> colors = [];
    private Guid loadedWallId;
    private bool loaded;
    private bool busy;
    private string? message;
    private string? failure;
    private IReadOnlyList<HoldProposal> proposals = [];

    /// <summary>The wall being administered.</summary>
    [Parameter]
    public Guid WallId { get; set; }

    /// <summary>Set when the page is viewed through a share link: nothing is offered there.</summary>
    [Parameter]
    public string? ShareToken { get; set; }

    [Inject]
    private IHoldProposalService Proposals { get; set; } = default!;

    [Inject]
    private IKioskContext KioskContext { get; set; } = default!;

    [Inject]
    private ILogger<WallHoldProposals> Logger { get; set; } = default!;

    private bool Hidden => KioskContext.IsKiosk || !string.IsNullOrEmpty(ShareToken);

    /// <summary>A proposal in a few words: photos, size, where.</summary>
    internal static string ProposalText(HoldProposal p)
    {
        var size = string.Create(CultureInfo.InvariantCulture, $"{p.SizeMm:0}");
        var where = p is { PanelX: { } x, PanelY: { } y } ? $", {PlainCopy.Position(x, y)} on its panel photo." : ".";
        return $"Seen in {PlainCopy.Plural(p.Views, "photo")}, about {size} mm wide{(p.H > 60 ? ", on a volume" : string.Empty)}{where}";
    }

    /// <summary>The spot of a proposal on the 3D wall, for "Show on wall".</summary>
    internal static WallSpot SpotOf(HoldProposal p) => new(p.FacetId, p.A, p.B, p.H);

    // Loaded here, not in OnInitializedAsync: WallDetail is retained across enhanced navigation between walls.
    protected override async Task OnParametersSetAsync()
    {
        if (WallId == loadedWallId || Hidden)
        {
            return;
        }

        loadedWallId = WallId;
        (loaded, proposals, message, failure) = (false, [], null, null);
        await ReloadAsync();
    }

    /// <summary>Loads the list; after the first list a failure keeps it (and the message) on screen.</summary>
    private async Task ReloadAsync()
    {
        try
        {
            proposals = await Proposals.ListAsync(WallId);
            foreach (var p in proposals)
            {
                colors.TryAdd(p.Id, string.Empty);
            }

            loaded = true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException or KioskRestrictedException)
        {
            Logger.LogWarning(ex, "Could not load the hold proposals of wall {WallId}", WallId);
            if (loaded)
            {
                failure ??= "The list could not be updated; reload the page to see the latest.";
            }
        }
    }

    // Crops decode a full photo each, so only the shown page is cropped, a few at a time, and never while prerendering.
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (cropping || !loaded || Hidden)
        {
            return;
        }

        var next = proposals.Take(shown).Where(p => !crops.ContainsKey(p.Id) && !cropFailed.Contains(p.Id)).Take(CropBatch).ToList();
        if (next.Count == 0)
        {
            return;
        }

        cropping = true;
        try
        {
            foreach (var p in next)
            {
                if (await CropOrNullAsync(p.Id) is { } jpeg)
                {
                    crops[p.Id] = "data:image/jpeg;base64," + Convert.ToBase64String(jpeg);
                }
                else
                {
                    cropFailed.Add(p.Id);
                }
            }
        }
        finally
        {
            cropping = false;
        }

        StateHasChanged();
    }

    private async Task<byte[]?> CropOrNullAsync(Guid proposalId)
    {
        try
        {
            return await Proposals.CropAsync(WallId, proposalId);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException or KioskRestrictedException)
        {
            Logger.LogWarning(ex, "Could not crop hold proposal {ProposalId}", proposalId);
            return null;
        }
    }

    private Task FindAsync() => RunAsync(async () =>
    {
        var r = await Proposals.FindAsync(WallId);
        return $"{r.Photos} photos searched: {r.Proposals} possible new holds ({r.OnPanels} on a wall photo).";
    });

    private Task AcceptAsync(HoldProposal p) => RunAsync(async () =>
    {
        var color = colors.GetValueOrDefault(p.Id);
        await Proposals.AcceptAsync(WallId, p.Id, string.IsNullOrEmpty(color) ? null : color, HoldCategory.Hand);
        return "Hold added to its wall photo.";
    });

    private Task RejectAsync(HoldProposal p) => RunAsync(async () =>
    {
        await Proposals.RejectAsync(WallId, p.Id);
        return "Noted: that spot will not be proposed again.";
    });

    private async Task RunAsync(Func<Task<string>> action)
    {
        busy = true;
        failure = null;
        message = null;
        try
        {
            message = await action();
            await ReloadAsync();
        }
        catch (Exception ex) when (ex is UserFacingException or KioskRestrictedException)
        {
            failure = ex.Message;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException)
        {
            Logger.LogWarning(ex, "Volume / hold proposal action on wall {WallId} failed", WallId);
            failure = UserFacingException.GenericMessage;
        }
        finally
        {
            busy = false;
        }
    }
}
