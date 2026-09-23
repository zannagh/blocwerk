// <copyright file="ShapeRecognitionStep.razor.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Microsoft.AspNetCore.Components;

namespace Blocwerk.Web.Components.Shared;

/// <summary>
/// The optional shape-recognition step of the big-wall update. The run itself lives on the server
/// (<see cref="IWallUpdateShapeService"/>), so this component only starts it, polls its progress once a
/// second while it is alive, and reports the outcome; closing the page does not stop it.
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

    private readonly CancellationTokenSource disposed = new();
    private ShapeRecognitionStatusInfo? _status;
    private ShapeRecognitionScope _scope = ShapeRecognitionScope.NewAndChanged;
    private bool _overwriteManual;
    private string? _error;
    private bool _polling;
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

    // Cancelled but not disposed: the poll loop may still be reading the token when this runs.
    public void Dispose() => disposed.Cancel();

    [Inject]
    private IWallUpdateShapeService Shapes { get; set; } = default!;

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

    private void EnsurePolling()
    {
        if (_polling || !IsRunning)
        {
            return;
        }

        _polling = true;
        _ = PollAsync();
    }

    private async Task PollAsync()
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            while (IsRunning && await timer.WaitForNextTickAsync(disposed.Token))
            {
                await InvokeAsync(async () =>
                {
                    await RefreshAsync();
                    StateHasChanged();
                });
            }
        }
        catch (OperationCanceledException)
        {
            // Component gone; the run carries on server-side.
        }
        finally
        {
            _polling = false;
        }
    }
}
