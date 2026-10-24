// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Diagnostics.CodeAnalysis;
using Microsoft.AspNetCore.Components;

namespace Blocwerk.Client.Views;

/// <summary>
/// The components mounted into the views' slots (<see cref="ViewSlotName"/>), registered at start-up in Program.cs.
/// Empty in PR 3.11; PR 3.13 registers its gallery and 3D components here and touches no view. A registered component
/// declares two parameters: <c>Context</c> (<see cref="ViewSlotContext"/>) and <c>DefaultContent</c> (a
/// <see cref="RenderFragment"/>: what the slot shows without it, the panel viewer on a stage slot, else nothing).
/// </summary>
public sealed class ViewSlotRegistry
{
    /// <summary>The parameter carrying the <see cref="ViewSlotContext"/>.</summary>
    public const string ContextParameter = "Context";

    /// <summary>The parameter carrying the slot's default content.</summary>
    public const string DefaultParameter = "DefaultContent";

    private readonly Dictionary<ViewSlotName, List<Func<ViewSlotContext, RenderFragment?, RenderFragment>>> slots = [];

    /// <summary>
    /// Mounts <typeparamref name="TComponent"/> into <paramref name="slot"/>, after any mounted before it; a stage slot
    /// takes one component only.
    /// </summary>
    /// <typeparam name="TComponent">The component (kept by the trimmer through this generic).</typeparam>
    /// <param name="slot">The slot.</param>
    /// <returns>This registry.</returns>
    public ViewSlotRegistry Add<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TComponent>(ViewSlotName slot)
        where TComponent : IComponent
    {
        if (!slots.TryGetValue(slot, out var list))
        {
            slots[slot] = list = [];
        }

        // A stage hands its panel viewer to what mounts on it: two components would each show it.
        if (slot is ViewSlotName.WallStage or ViewSlotName.BoulderStage && list.Count > 0)
        {
            throw new InvalidOperationException($"{slot} takes one component; {list.Count} is mounted already.");
        }

        list.Add((context, fallback) => builder =>
        {
            builder.OpenComponent<TComponent>(0);
            builder.AddComponentParameter(1, ContextParameter, context);
            builder.AddComponentParameter(2, DefaultParameter, fallback);
            builder.CloseComponent();
        });
        return this;
    }

    /// <summary>The mounted components of <paramref name="slot"/>, in registration order, ready to render.</summary>
    /// <param name="slot">The slot.</param>
    /// <returns>A fragment per component, given the context and the default content; none for an empty slot.</returns>
    public IReadOnlyList<Func<ViewSlotContext, RenderFragment?, RenderFragment>> For(ViewSlotName slot) =>
        slots.TryGetValue(slot, out var list) ? list : [];
}
