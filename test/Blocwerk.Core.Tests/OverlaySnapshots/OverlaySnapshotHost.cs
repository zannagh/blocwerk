// <copyright file="OverlaySnapshotHost.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Reflection;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;

namespace Blocwerk.Core.Tests.OverlaySnapshots;

/// <summary>
/// Renders one component and keeps hold of it, so a snapshot can put it into a state only a click reaches (the heat
/// map, "Show all holds") and render it again: static rendering has no events.
/// </summary>
internal sealed class OverlaySnapshotHost : ComponentBase
{
    private static readonly MethodInfo Refresh = typeof(ComponentBase).GetMethod("StateHasChanged", BindingFlags.Instance | BindingFlags.NonPublic)!;

    /// <summary>The component type to render.</summary>
    [Parameter]
    public Type Component { get; set; } = typeof(ComponentBase);

    /// <summary>Its parameters.</summary>
    [Parameter]
    public IReadOnlyDictionary<string, object?> Values { get; set; } = new Dictionary<string, object?>();

    /// <summary>The rendered component, once it has rendered.</summary>
    [Parameter]
    public Action<object>? Rendered { get; set; }

    /// <summary>Sets the private field <paramref name="field"/> of <paramref name="component"/> and renders it again.</summary>
    public static void Set(object component, string field, object value)
    {
        component.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(component, value);
        Refresh.Invoke(component, null);
    }

    /// <inheritdoc />
    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        builder.OpenComponent(0, Component);
        builder.AddMultipleAttributes(1, Values!);
        builder.AddComponentReferenceCapture(2, instance => Rendered?.Invoke(instance));
        builder.CloseComponent();
    }
}
