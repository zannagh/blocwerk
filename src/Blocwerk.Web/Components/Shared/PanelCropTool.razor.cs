// <copyright file="PanelCropTool.razor.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Globalization;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Services.PanelCrop;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Blocwerk.Web.Components.Shared;

/// <summary>
/// The wall editor's crop tool for one live panel photo: a draggable crop box with a live preview of the holds it
/// would cut, the "are you sure?" step for a crop that removes holds, and "Undo crop". The server does the crop
/// (<see cref="IPanelCropService"/>); this component only picks the rectangle and reports the new photo revision.
/// </summary>
public partial class PanelCropTool : IAsyncDisposable
{
    private static readonly (string Handle, string Label)[] Handles =
    [
        ("l", "Left edge"), ("r", "Right edge"), ("t", "Top edge"), ("b", "Bottom edge"),
        ("tl", "Top left corner"), ("tr", "Top right corner"), ("bl", "Bottom left corner"), ("br", "Bottom right corner"),
    ];

    private ElementReference stage;
    private ElementReference box;
    private IJSObjectReference? module;
    private IJSObjectReference? handle;
    private DotNetObjectReference<PanelCropTool>? self;
    private PanelCropRect rect = PanelCropRect.Full;
    private HashSet<Guid> cutHoldIds = [];
    private PanelCropState? state;
    private PanelCropPreview? confirm;
    private string? error;
    private bool busy;

    /// <summary>Gets or sets the wall.</summary>
    [Parameter]
    [EditorRequired]
    public Guid WallId { get; set; }

    /// <summary>Gets or sets the live panel to crop.</summary>
    [Parameter]
    [EditorRequired]
    public Guid PanelId { get; set; }

    /// <summary>Gets or sets the panel's live photo URL as the editor shows it.</summary>
    [Parameter]
    [EditorRequired]
    public string PhotoUrl { get; set; } = string.Empty;

    /// <summary>Gets or sets the panel's live holds, in the photo's current frame, for the cut preview.</summary>
    [Parameter]
    public IReadOnlyList<Hold> Holds { get; set; } = [];

    /// <summary>Gets or sets the callback after a crop or undo was saved; carries the new photo revision.</summary>
    [Parameter]
    public EventCallback<int> OnSaved { get; set; }

    /// <summary>Gets or sets the callback when the tool closes without saving.</summary>
    [Parameter]
    public EventCallback OnClose { get; set; }

    [Inject]
    private IPanelCropService CropService { get; set; } = null!;

    [Inject]
    private IJSRuntime JS { get; set; } = null!;

    /// <summary>Called by panel-crop.js with the box as it is now (throttled while dragging).</summary>
    /// <param name="left">Left edge (0..1).</param>
    /// <param name="top">Top edge (0..1).</param>
    /// <param name="width">Width (0..1).</param>
    /// <param name="height">Height (0..1).</param>
    [JSInvokable]
    public void OnCropRect(double left, double top, double width, double height)
    {
        rect = new PanelCropRect(left, top, width, height);
        error = null;
        UpdateCutPreview();
        StateHasChanged();
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        try
        {
            if (handle is not null)
            {
                await handle.InvokeVoidAsync("dispose");
                await handle.DisposeAsync();
            }

            if (module is not null)
            {
                await module.DisposeAsync();
            }
        }
        catch (JSDisconnectedException)
        {
            // The circuit is gone; so is the page the box lived on.
        }

        self?.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <inheritdoc/>
    protected override async Task OnInitializedAsync()
    {
        state = await CropService.GetStateAsync(WallId, PanelId);
    }

    /// <inheritdoc/>
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender)
        {
            return;
        }

        self = DotNetObjectReference.Create(this);
        module = await JS.InvokeAsync<IJSObjectReference>("import", "/js/panel-crop.js");
        handle = await module.InvokeAsync<IJSObjectReference>("attach", stage, box, self, JsRect(rect));
    }

    private static string F(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    private static string Percent(double fraction) => (fraction * 100).ToString("0", CultureInfo.InvariantCulture) + "%";

    private static string Plural(int count, string noun) => count == 1 ? $"1 {noun}" : $"{count} {noun}s";

    private static object JsRect(PanelCropRect r) => new { left = r.Left, top = r.Top, width = r.Width, height = r.Height };

    private void UpdateCutPreview()
    {
        if (rect.Width <= 0 || rect.Height <= 0 || rect.IsFull)
        {
            cutHoldIds = [];
            return;
        }

        var map = PanelFrameMap.IntoCrop(rect);
        cutHoldIds = Holds.Where(h => PanelCropCutoff.IsCutOff(map.Mapped(h))).Select(h => h.Id).ToHashSet();
    }
}
