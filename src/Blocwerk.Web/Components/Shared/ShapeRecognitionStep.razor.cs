// <copyright file="ShapeRecognitionStep.razor.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Enums;
using Blocwerk.Core.Pulse;
using Blocwerk.Core.Services;
using Blocwerk.Web.State;
using Blocwerk.Web.State.Live;
using Microsoft.AspNetCore.Components;

namespace Blocwerk.Web.Components.Shared;

/// <summary>
/// The optional shape-recognition step of the big-wall update. The run itself lives on the server
/// (<see cref="IWallUpdateShapeService"/>), so this component only starts it, follows its progress on the session's
/// pulses (<see cref="PulseLoop"/>, with a safety poll while it is alive), and reports the outcome; closing the page does
/// not stop it.
/// </summary>
public partial class ShapeRecognitionStep : IDisposable
{
    private static readonly (ShapeRecognitionScope Scope, string Label, string Detail)[] ScopeOptions =
    [
        (ShapeRecognitionScope.NewAndChanged, "New and changed holds", "recommended"),
        (ShapeRecognitionScope.New, "Only new holds", "no old hold carried onto them"),
        (ShapeRecognitionScope.Changed, "Only changed holds", "carried with the verdict “changed”"),
        (ShapeRecognitionScope.All, "All holds on the new photos", "slowest"),
    ];

    private readonly PulseHolder<PulseLoop> pulses = new();
    private ShapeRecognitionStatusInfo? _status;
    private ShapeRecognitionScope _scope = ShapeRecognitionScope.NewAndChanged;
    private bool _overwriteManual;
    private string? _error;
    private (Guid, Guid?)? _loadedFor;

    [Parameter]

    public Guid WallId { get; set; }

    /// <summary>The update session this circuit works on; every write is refused once it is not the open one.</summary>
    [Parameter]
    public Guid? SessionId { get; set; }

    [Parameter]

    public EventCallback OnReview { get; set; }

    [Parameter]

    public EventCallback OnSkip { get; set; }

    /// <summary>Whether a run is alive right now (an interrupted one is offered as a restart instead).</summary>
    public bool IsRunning => _status is { Status: ShapeRecognitionStatus.Running, Interrupted: false };

    /// <summary>Whether the run completed, so Enter means "review".</summary>
    public bool IsCompleted => _status?.Status == ShapeRecognitionStatus.Completed;

    /// <summary>The wizard's Enter: review a completed run, otherwise start one. Nothing while running.</summary>
    public async Task PrimaryAsync()
    {
        if (_status is null || IsRunning)
        {
            return;
        }

        if (IsCompleted)
        {
            await OnReview.InvokeAsync();
        }
        else if (_status.Available)
        {
            await StartAsync(rerun: false);
        }
    }

    /// <summary>Skips the step: every hold keeps the shape it has now.</summary>
    public async Task SkipAsync()
    {
        if (await CallAsync(() => Shapes.SkipAsync(WallId, SessionId)))
        {
            await OnSkip.InvokeAsync();
        }
    }

    public void Dispose() => pulses.Dispose();

    [Inject]
    private IWallUpdateShapeService Shapes { get; set; } = default!;

    [Inject]
    private WallCacheState Cache { get; set; } = default!;

    [Inject]
    private IWallPulse Pulse { get; set; } = default!;

    [Inject]
    private ILogger<ShapeRecognitionStep> Logger { get; set; } = default!;

    // Enhanced-nav rule: parameters can change on a retained instance, so the status load lives here —
    // once per (wall, session), not on every parent render, or the user's scope choice would reset.
    protected override async Task OnParametersSetAsync()
    {
        if (_loadedFor == (WallId, SessionId))
        {
            return;
        }

        _loadedFor = (WallId, SessionId);
        await RefreshAsync();
        _scope = _status?.Scope ?? _scope;
        _overwriteManual = _status?.OverwriteManual ?? false;
        EnsurePolling();
    }

    private async Task StartAsync(bool rerun)
    {
        await CallAsync(() => Shapes.StartRecognitionAsync(
            WallId, new ShapeRecognitionOptions(_scope, _overwriteManual, rerun), SessionId));
        EnsurePolling();
    }

    private async Task RefreshAsync()
    {
        await CallAsync(() => Shapes.GetStatusAsync(WallId));
    }

    private async Task<bool> CallAsync(Func<Task<ShapeRecognitionStatusInfo>> call)
    {
        try
        {
            _status = await call();
            _error = null;
            return true;
        }
        catch (Exception ex)
        {
            _error = ex.Message;
            return false;
        }
    }

    // One loop per wall; it idles while no run is alive. Called after awaits: once disposed, the holder opens nothing.
    private void EnsurePolling()
    {
        if (pulses.IsFor(WallId))
        {
            pulses.Current?.Nudge();
            return;
        }

        pulses.Open(WallId, () => PulseLoop.Start(Cache, Pulse, WallId, [PulseTopics.Update], () => IsRunning, InvokeAsync, RefreshAndRenderAsync, Logger));
    }

    private async Task RefreshAndRenderAsync()
    {
        await RefreshAsync();
        StateHasChanged();
    }
}
