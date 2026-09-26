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
    private readonly Dictionary<Guid, string> crops = [];
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
    internal static string ProposalText(HoldProposal p) =>
        string.Create(CultureInfo.InvariantCulture, $"Seen in {p.Views} photos, about {p.SizeMm:0} mm, facet {p.FacetId}{(p.H > 60 ? ", on a volume" : string.Empty)}.");

    // Loaded here, not in OnInitializedAsync: WallDetail is retained across enhanced navigation between walls.
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

    private async Task ReloadAsync()
    {
        try
        {
            proposals = await Proposals.ListAsync(WallId);
            foreach (var p in proposals.Where(p => !crops.ContainsKey(p.Id)))
            {
                colors.TryAdd(p.Id, string.Empty);
                if (await Proposals.CropAsync(WallId, p.Id) is { } jpeg)
                {
                    crops[p.Id] = "data:image/jpeg;base64," + Convert.ToBase64String(jpeg);
                }
            }

            loaded = true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException or KioskRestrictedException)
        {
            Logger.LogWarning(ex, "Could not load the hold proposals of wall {WallId}", WallId);
            loaded = false;
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
