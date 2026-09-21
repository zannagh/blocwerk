// <copyright file="FieldUpdate.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.Services;

/// <summary>
/// A tri-state field in a partial update: either "leave this field alone" or "write exactly this
/// value" — where the value may itself be <c>null</c>, meaning "clear it".
/// <para>
/// Nullable parameters cannot express that third state: <c>null</c> has to mean either "untouched"
/// or "cleared", and a method taking a dozen of them inevitably picks a different answer per field.
/// <c>UpdateHoldAsync</c>'s parameter list did exactly that — colour/material/hand-type cleared on
/// null while category/name/shape were left alone — which makes any partial payload (the toolbar's
/// property stamp, for one) silently wipe the fields it did not mean to carry.
/// </para>
/// </summary>
/// <typeparam name="T">The field's type.</typeparam>
public readonly struct FieldUpdate<T>
{
    private FieldUpdate(T value)
    {
        Value = value;
        HasValue = true;
    }

    /// <summary>True when this update carries a value to write; false means "leave the field alone".</summary>
    public bool HasValue { get; }

    /// <summary>The value to write. Only meaningful when <see cref="HasValue"/> is true.</summary>
    public T Value { get; }

    /// <summary>Leave the field as it is. This is also the <c>default</c>, so an omitted field is untouched.</summary>
    public static FieldUpdate<T> Keep => default;

    /// <summary>Write <paramref name="value"/>, including when it is <c>null</c> — that clears the field.</summary>
    public static FieldUpdate<T> Set(T value) => new(value);

    /// <summary>
    /// Assigning a bare value means <see cref="Set"/>, so a caller sending the full intended state can
    /// write <c>Color = hold.Color</c>. Note that this makes <c>Color = null</c> a CLEAR — use
    /// <see cref="Keep"/> (or simply omit the field) to leave it alone.
    /// </summary>
    public static implicit operator FieldUpdate<T>(T value) => Set(value);

    /// <summary>The value to write, or <paramref name="current"/> when this update leaves the field alone.</summary>
    public T Or(T current) => HasValue ? Value : current;
}
