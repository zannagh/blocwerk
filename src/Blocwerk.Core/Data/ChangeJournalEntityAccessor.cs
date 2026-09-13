using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Blocwerk.Core.Data;

/// <summary>
/// Generic, metadata-driven access used by revert and replay to resolve, compare, apply, insert and
/// delete allow-listed rows by their recorded <c>KeyJson</c> / before / after images — without any
/// entity-specific code, so composite keys (e.g. BoulderHold) are handled the same as single keys.
/// </summary>
internal static class ChangeJournalEntityAccessor
{
    /// <summary>Maps a recorded <c>EntityType</c> name back to its CLR type on this model, or null.</summary>
    public static Type? ResolveType(DbContext context, string entityTypeName)
    {
        return context.Model.GetEntityTypes()
            .FirstOrDefault(t => t.ClrType.Name == entityTypeName)?.ClrType;
    }

    /// <summary>Finds the tracked-or-loaded row for a recorded key, or null when it does not exist.</summary>
    public static async ValueTask<object?> FindAsync(
        DbContext context, Type clrType, IEntityType entityType, string keyJson, CancellationToken cancellationToken)
    {
        var keyValues = ChangeJournalValueWriter.DeserializeKey(entityType, keyJson);
        return await context.FindAsync(clrType, keyValues, cancellationToken);
    }

    /// <summary>
    /// True when the entity's CURRENT values equal every property in <paramref name="expected"/>.
    /// byte[] columns compare by content; on the first mismatch <paramref name="detail"/> names it.
    /// </summary>
    public static bool CurrentMatches(EntityEntry entry, Dictionary<string, object?> expected, out string? detail)
    {
        foreach (var (name, expectedValue) in expected)
        {
            var property = entry.Property(name);
            if (!ValueEquals(property.Metadata, property.CurrentValue, expectedValue))
            {
                detail = $"'{name}' differs (expected {Describe(expectedValue)}, found {Describe(property.CurrentValue)})";
                return false;
            }
        }

        detail = null;
        return true;
    }

    /// <summary>Sets each supplied property's current value on the tracked entity.</summary>
    public static void ApplyValues(EntityEntry entry, Dictionary<string, object?> values)
    {
        foreach (var (name, value) in values)
        {
            entry.Property(name).CurrentValue = value;
        }
    }

    /// <summary>Creates a fresh instance from a full before/after image and stages it as an insert.</summary>
    public static void InsertFrom(DbContext context, Type clrType, Dictionary<string, object?> values)
    {
        var instance = Activator.CreateInstance(clrType)
            ?? throw new InvalidOperationException($"Cannot instantiate {clrType.Name}.");
        var entry = context.Add(instance);
        ApplyValues(entry, values);
    }

    /// <summary>
    /// Structural value equality for a recorded property image. byte[] compares by content; every
    /// other property is compared through the EF property's own <see cref="ValueComparer"/> when it
    /// has one — this is what makes JSON-converted collection columns (e.g. <c>Hold.ShapePoints</c>,
    /// <c>Wall.BorderPoints</c>, both stored as <c>List&lt;ShapePoint&gt;</c>) compare by GEOMETRY
    /// rather than by reference. <see cref="ChangeJournalValueWriter.DeserializeValues"/> already
    /// rebuilds each expected value as the property's CLR type, so the comparer receives operands it
    /// accepts; we only invoke it when both operands are instances of the comparer's type and fall
    /// back to <see cref="object.Equals(object?, object?)"/> otherwise.
    /// </summary>
    private static bool ValueEquals(IProperty property, object? current, object? expected)
    {
        if (current is byte[] a && expected is byte[] b)
        {
            return a.AsSpan().SequenceEqual(b);
        }

        if (current is null || expected is null)
        {
            return current is null && expected is null;
        }

        var comparer = property.GetValueComparer();
        if (comparer is not null
            && comparer.Type.IsInstanceOfType(current)
            && comparer.Type.IsInstanceOfType(expected))
        {
            return comparer.Equals(current, expected);
        }

        return current.Equals(expected);
    }

    private static string Describe(object? value)
    {
        return value switch
        {
            null => "null",
            byte[] bytes => $"byte[{bytes.Length}]",
            _ => value.ToString() ?? "?",
        };
    }
}
