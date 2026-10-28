// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Client.Store;
using Blocwerk.Client.ThreeD;
using Blocwerk.Client.Views;
using Blocwerk.Core.Tests.OfflineClient3D;
using Blocwerk.Core.Tests.OfflineClientStore;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.Web.HtmlRendering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Blocwerk.Core.Tests.OfflineClientViews;

/// <summary>
/// Renders a client page to HTML over a started <see cref="ClientStore"/> and keeps it live: a store change re-renders
/// it, so <see cref="WaitForAsync"/> sees what the page shows after the next sync.
/// </summary>
internal sealed class ViewHarness : IAsyncDisposable
{
    private readonly ServiceProvider services;
    private readonly HtmlRenderer renderer;
    private HtmlRootComponent? root;

    private ViewHarness(ClientStore store, ServiceProvider services)
    {
        Store = store;
        this.services = services;
        renderer = new HtmlRenderer(services, NullLoggerFactory.Instance);
    }

    public ClientStore Store { get; }

    public static async Task<ViewHarness> StartAsync(
        FakeStoreSource source, string path, ViewSlotRegistry? slots = null, IClient3D? client3D = null, View3DToggle? toggle = null)
    {
        var store = new ClientStore(source);
        await store.StartAsync();
        var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton(store)
            .AddSingleton<IClientStoreSource>(source)
            .AddSingleton<WallSummaries>()
            .AddSingleton<IErrorBoundaryLogger, SilentErrorBoundaryLogger>()
            .AddSingleton<IClientViewEnv, FakeViewEnv>()
            .AddSingleton(client3D ?? new FakeClient3D())
            .AddSingleton(toggle ?? new View3DToggle())
            .AddSingleton(slots ?? new ViewSlotRegistry())
            .AddSingleton<NavigationManager>(new TestNavigation(path))
            .AddSupplyValueFromQueryProvider()
            .BuildServiceProvider();
        return new ViewHarness(store, services);
    }

    /// <summary>Renders <typeparamref name="TPage"/> with <paramref name="parameters"/> and returns its HTML.</summary>
    public async Task<string> RenderAsync<TPage>(Dictionary<string, object?> parameters)
        where TPage : IComponent
    {
        root = await renderer.Dispatcher.InvokeAsync(() => renderer.RenderComponentAsync<TPage>(ParameterView.FromDictionary(parameters)));
        await root.Value.QuiescenceTask;
        return await HtmlAsync();
    }

    public Task<string> HtmlAsync() => renderer.Dispatcher.InvokeAsync(() => root!.Value.ToHtmlString());

    /// <summary>The page's HTML once <paramref name="shows"/> holds (the page re-reads the store asynchronously).</summary>
    public async Task<string> WaitForAsync(Func<string, bool> shows)
    {
        for (var i = 0; i < 100; i++)
        {
            var html = await HtmlAsync();
            if (shows(html))
            {
                return html;
            }

            await Task.Delay(20);
        }

        return await HtmlAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await renderer.DisposeAsync();
        await services.DisposeAsync();
    }
}
