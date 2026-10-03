// <copyright file="HoldShapeRenderer.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Web.Components.Shared;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Blocwerk.Core.Tests;

/// <summary>Renders <see cref="HoldShape"/> to static HTML with the framework's own renderer.</summary>
internal static class HoldShapeRenderer
{
    /// <summary>Renders the component with the given parameters and returns its markup.</summary>
    public static string Render(Dictionary<string, object?> parameters)
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        using var renderer = new HtmlRenderer(services, NullLoggerFactory.Instance);
        return renderer.Dispatcher.InvokeAsync(async () =>
        {
            var output = await renderer.RenderComponentAsync<HoldShape>(ParameterView.FromDictionary(parameters));
            return output.ToHtmlString();
        }).GetAwaiter().GetResult();
    }
}
