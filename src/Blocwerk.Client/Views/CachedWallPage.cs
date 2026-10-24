// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Client.Store;
using Microsoft.AspNetCore.Components;

namespace Blocwerk.Client.Views;

/// <summary>
/// A page that shows one cached wall: it reads the wall from <see cref="ClientStore"/> in
/// <see cref="OnParametersSetAsync"/> (the router keeps the instance across two walls on one template), and again on
/// every store change, so a replaced document or a feed event (the sync loop, PR 3.12) shows at once and a wall gone
/// from the device shows as not saved. It reads nothing else and writes nothing.
/// </summary>
public abstract class CachedWallPage : ComponentBase, IDisposable
{
    private int loads;

    /// <summary>Gets or sets the wall.</summary>
    [Parameter]
    public Guid WallId { get; set; }

    /// <summary>Gets or sets the share link's token on a shared route, else null.</summary>
    [Parameter]
    public string? ShareToken { get; set; }

    /// <summary>Gets or sets the store.</summary>
    [Inject]
    protected ClientStore Store { get; set; } = null!;

    /// <summary>Gets or sets the browser facts.</summary>
    [Inject]
    protected IClientViewEnv Env { get; set; } = null!;

    /// <summary>Gets the cached wall, or null when the device holds none for this path.</summary>
    protected CachedWall? Wall { get; private set; }

    /// <summary>Gets a value indicating whether the first read has ended.</summary>
    protected bool Loaded { get; private set; }

    /// <summary>Gets the browser facts (the defaults until read).</summary>
    protected ClientViewFacts Facts { get; private set; } = ClientViewFacts.Default;

    /// <inheritdoc />
    public void Dispose()
    {
        Store.Changed -= OnStoreChanged;
        GC.SuppressFinalize(this);
    }

    /// <inheritdoc />
    protected override void OnInitialized() => Store.Changed += OnStoreChanged;

    /// <inheritdoc />
    protected override async Task OnParametersSetAsync()
    {
        Facts = await Env.FactsAsync();
        await LoadAsync();
    }

    /// <summary>Called after every read, with <see cref="Wall"/> set: the page derives its view of it.</summary>
    protected abstract void OnWallRead();

    private async Task LoadAsync()
    {
        var load = ++loads;
        var wall = await Store.GetWallAsync(WallId);
        if (load != loads)
        {
            return;
        }

        Wall = wall?.Wall is null ? null : wall;
        Loaded = true;
        OnWallRead();
    }

    private void OnStoreChanged() => _ = InvokeAsync(async () =>
    {
        await LoadAsync();
        StateHasChanged();
    });
}
