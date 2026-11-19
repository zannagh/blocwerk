// <copyright file="BoulderCreate.Variant.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Data.Common;
using Blocwerk.Core.Helpers;
using Blocwerk.Core.Services;
using Microsoft.AspNetCore.Components;

namespace Blocwerk.Web.Components.Pages.Walls;

/// <summary>
/// Add Variant: the create form opened with <c>?variantOf=</c> starts from that boulder's holds, marks, rules and grade.
/// Only the form is filled in here; the original is never written to.
/// </summary>
public partial class BoulderCreate
{
    private Guid? _variantOriginId;
    private string? _variantOriginName;

    /// <summary>Gets or sets the boulder this create starts from, if any.</summary>
    [SupplyParameterFromQuery(Name = "variantOf")]
    public Guid? VariantOf { get; set; }

    private string PageHeading => _variantOriginId is null ? "Create Boulder" : "Add Variant";

    private string BackUrl => _variantOriginId is { } id ? $"/walls/{WallId}/boulders/{id}" : $"/walls/{WallId}";

    private string VariantKeySuffix => VariantOf is { } id ? $":variant:{id}" : string.Empty;

    private string VariantQuery => VariantOf is { } id ? $"?variantOf={id}" : string.Empty;

    /// <summary>Fills the form from the origin; an origin that cannot be read leaves an ordinary create.</summary>
    private async Task ApplyVariantOriginAsync()
    {
        if (VariantOf is not { } originId || await ReadVariantOriginAsync(originId) is not { } origin)
        {
            return;
        }

        _variantOriginId = origin.Id;
        _variantOriginName = origin.Name;
        _name = BoulderVariantNames.Suggest(origin.Name, _existingNames);
        _grade = origin.Grade;
        _preferFont = GradeScale.IsFont(origin.Grade) ?? _preferFont;
        _allKickboardOn = origin.KickboardFootholdsOn;
        _handsFollowFeet = origin.HandsFollowFeet;
        _footColorOnly = origin.FootColorOnly;
        _noMatch = origin.NoMatch;
        _customRule = origin.CustomRule;
        _styles = origin.Styles;

        var prefill = await BoulderService.GetRevisePrefillAsync(origin.Id, _holds, _panels);
        _selectedHolds = prefill?.Picks.Select(p => new BoulderHoldInput(p.HoldId, p.Type, p.Usage)).ToList()
            ?? origin.BoulderHolds.Select(h => new BoulderHoldInput(h.HoldId, h.Type, h.Usage)).ToList();
        _currentPanel = _panels.FirstOrDefault(p => p.Id == prefill?.StartPanelId) ?? _currentPanel;
    }

    private async Task<Blocwerk.Core.Entities.Boulder?> ReadVariantOriginAsync(Guid originId)
    {
        try
        {
            var origin = await BoulderService.GetBoulderAsync(originId);
            return origin is { IsDraft: false } && origin.WallId == WallId ? origin : null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or DbException or TimeoutException or UnauthorizedAccessException)
        {
            Logger.LogWarning(ex, "Could not read boulder {BoulderId} to start a variant from", originId);
            return null;
        }
    }
}
