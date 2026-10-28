// <copyright file="OverlaySnapshotRenderer.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Runtime.CompilerServices;
using Blocwerk.Core.Services.HybridZoom;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;
using NSubstitute;

namespace Blocwerk.Core.Tests.OverlaySnapshots;

/// <summary>
/// Renders an overlay component to static HTML with the framework's own renderer, and compares it with the stored
/// snapshot byte for byte. <c>BLOCWERK_WRITE_OVERLAY_SNAPSHOTS=1</c> writes the snapshots instead: they were written
/// once from the Server's own overlay code before it moved into Blocwerk.Shared.Ui, and must never be rewritten to make
/// a change pass.
/// </summary>
internal static class OverlaySnapshotRenderer
{
    private const string WriteVariable = "BLOCWERK_WRITE_OVERLAY_SNAPSHOTS";

    /// <summary>Renders <typeparamref name="T"/> with <paramref name="parameters"/>.</summary>
    public static async Task<string> RenderAsync<T>(Dictionary<string, object?> parameters)
        where T : IComponent
    {
        await using var provider = Services();
        await using var renderer = new HtmlRenderer(provider, NullLoggerFactory.Instance);
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var output = await renderer.RenderComponentAsync<T>(ParameterView.FromDictionary(parameters));
            return output.ToHtmlString();
        });
    }

    /// <summary>
    /// Renders <typeparamref name="T"/>, then sets each of <paramref name="fields"/> (private state a click would set)
    /// and renders it again; returns the final markup.
    /// </summary>
    public static async Task<string> RenderInStateAsync<T>(Dictionary<string, object?> parameters, params (string Field, object Value)[] fields)
        where T : IComponent
    {
        await using var provider = Services();
        await using var renderer = new HtmlRenderer(provider, NullLoggerFactory.Instance);
        object? component = null;
        var host = new Dictionary<string, object?>
        {
            [nameof(OverlaySnapshotHost.Component)] = typeof(T),
            [nameof(OverlaySnapshotHost.Values)] = parameters,
            [nameof(OverlaySnapshotHost.Rendered)] = (Action<object>)(c => component = c),
        };
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var output = await renderer.RenderComponentAsync<OverlaySnapshotHost>(ParameterView.FromDictionary(host));
            foreach (var (field, value) in fields)
            {
                OverlaySnapshotHost.Set(component!, field, value);
            }

            await output.QuiescenceTask;
            return output.ToHtmlString();
        });
    }

    /// <summary>Asserts <paramref name="html"/> equals the snapshot <paramref name="name"/>, with the wall id masked.</summary>
    public static void AssertMatches(string name, string html, Guid wallId, [CallerFilePath] string source = "")
    {
        var masked = html.Replace(wallId.ToString(), "{wall}", StringComparison.Ordinal);
        var path = Path.Combine(Path.GetDirectoryName(source)!, "Snapshots", name + ".html");
        if (Environment.GetEnvironmentVariable(WriteVariable) == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, masked);
            return;
        }

        Assert.True(File.Exists(path), $"No snapshot {name}.html; it is written from the pre-move overlay code only.");
        Assert.Equal(File.ReadAllText(path), masked);
    }

    private static ServiceProvider Services()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Substitute.For<IJSRuntime>());
        services.AddSingleton(new HybridZoomOptions { Enabled = false });
        return services.BuildServiceProvider();
    }
}
