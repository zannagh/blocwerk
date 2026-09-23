// <copyright file="SignedAngleInput.razor.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Globalization;
using Blocwerk.Web.Components.Shared.MarkerPlanner;
using Microsoft.AspNetCore.Components;

namespace Blocwerk.Web.Components.Shared;

/// <summary>A signed angle as a direction plus a positive size, so phones never need a minus key.</summary>
public partial class SignedAngleInput
{
    /// <summary>A direction picked while the value is empty/zero and the option has no default size.</summary>
    private int? pendingSign;

    /// <summary>The signed angle in degrees; null = not given (only with <see cref="AllowEmpty"/>).</summary>
    [Parameter]
    public double? Value { get; set; }

    /// <summary>Raised with the new signed angle.</summary>
    [Parameter]
    public EventCallback<double?> ValueChanged { get; set; }

    /// <summary>The directions, in display order. Exactly one should have sign 0.</summary>
    [Parameter]
    [EditorRequired]
    public IReadOnlyList<SignedAngleOption> Options { get; set; } = [];

    /// <summary>The largest accepted size.</summary>
    [Parameter]
    public double Max { get; set; } = 90;

    /// <summary>When true a size equal to <see cref="Max"/> is refused (e.g. a 90° "overhang" is a roof, not a wall).</summary>
    [Parameter]
    public bool MaxExclusive { get; set; }

    /// <summary>The size field's step.</summary>
    [Parameter]
    public double Step { get; set; } = 1;

    /// <summary>Select + number (table cells) instead of the button toggle.</summary>
    [Parameter]
    public bool Compact { get; set; }

    /// <summary>Offer "—" and allow clearing the size back to null.</summary>
    [Parameter]
    public bool AllowEmpty { get; set; }

    [Parameter]
    public bool Disabled { get; set; }

    /// <summary>The size input's id (full mode).</summary>
    [Parameter]
    public string? Id { get; set; }

    /// <summary>Accessible name of the control (compact mode).</summary>
    [Parameter]
    public string AriaLabel { get; set; } = "Angle";

    /// <summary>The id of the element labelling the toggle group (full mode).</summary>
    [Parameter]
    public string? LabelledBy { get; set; }

    /// <summary>The value in words, shown under the size (full mode).</summary>
    [Parameter]
    public string? Description { get; set; }

    /// <summary>Rendered beside the size (full mode), e.g. a drawing.</summary>
    [Parameter]
    public RenderFragment? ChildContent { get; set; }

    /// <summary>The sign of the stored value (exact, so a 1° slab being typed stays a slab); null when empty.</summary>
    private int? StoredSign => Value is not { } v ? null : Math.Sign(v);

    private int? EffectiveSign => pendingSign ?? StoredSign;

    private SignedAngleOption? ActiveOption => Options.FirstOrDefault(o => o.Sign == EffectiveSign);

    private string SizeText => Value is { } v && v != 0 ? NetCanvasModel.F(Math.Abs(v)) : string.Empty;

    protected override void OnParametersSet()
    {
        if (StoredSign is { } s && s != 0)
        {
            pendingSign = null;
        }
    }

    /// <summary>Switching keeps the angle's size; from zero it starts at the option's default (or waits for a size).</summary>
    private Task Choose(SignedAngleOption option)
    {
        pendingSign = null;
        if (option.Sign == 0)
        {
            return Emit(0);
        }

        var size = Math.Abs(Value ?? 0);
        if (size == 0)
        {
            if (option.DefaultMagnitude is not { } fallback)
            {
                pendingSign = option.Sign;
                return Task.CompletedTask;
            }

            size = fallback;
        }

        return Emit(option.Sign * Math.Abs(size));
    }

    private Task OnSelect(ChangeEventArgs e)
    {
        var text = e.Value?.ToString();
        if (string.IsNullOrEmpty(text))
        {
            pendingSign = null;
            return AllowEmpty ? Emit(null) : Task.CompletedTask;
        }

        var option = int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var sign)
            ? Options.FirstOrDefault(o => o.Sign == sign)
            : null;
        return option is null ? Task.CompletedTask : Choose(option);
    }

    /// <summary>A positive size under the chosen direction; 0 means the zero direction, a typed minus flips to the negative one.</summary>
    private Task OnSize(ChangeEventArgs e)
    {
        if (PlannerInput.Number(e.Value) is not { } typed)
        {
            return AllowEmpty && string.IsNullOrWhiteSpace(e.Value?.ToString()) ? Emit(null) : Task.CompletedTask;
        }

        var size = Math.Abs(typed);
        if (size > Max || (MaxExclusive && size >= Max))
        {
            return Task.CompletedTask;
        }

        var sign = typed < 0 ? -1 : EffectiveSign ?? 0;
        pendingSign = null;
        return Emit(sign * size);
    }

    private Task Emit(double? value) => value == Value ? Task.CompletedTask : ValueChanged.InvokeAsync(value);

    private static string F(double value) => NetCanvasModel.F(value);
}
