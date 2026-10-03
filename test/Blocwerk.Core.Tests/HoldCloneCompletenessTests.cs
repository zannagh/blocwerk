// <copyright file="HoldCloneCompletenessTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Reflection;
using Blocwerk.Core.Entities;

namespace Blocwerk.Core.Tests;

/// <summary>
/// <see cref="Hold.Clone"/> backs the clone-before-edit caching pattern and the next-generation carry:
/// a field it forgets is silently dropped. These tests walk every settable property by reflection, so a
/// property added to <see cref="Hold"/> but not to <see cref="Hold.Clone"/> fails here rather than in prod.
/// </summary>
public class HoldCloneCompletenessTests
{
    /// <summary>Navigations: deliberately NOT copied (the clone is detached). Everything else must be.</summary>
    private static readonly HashSet<string> NotCloned =
    [
        nameof(Hold.Wall),
        nameof(Hold.WallPanel),
        nameof(Hold.BoulderHolds),
    ];

    private static readonly string[] GlyphMetricProperties =
    [
        nameof(Hold.WidthMm),
        nameof(Hold.HeightMm),
        nameof(Hold.AreaMm2),
        nameof(Hold.FacetId),
        nameof(Hold.PlaneAMm),
        nameof(Hold.PlaneBMm),
        nameof(Hold.FingerprintJson),
        nameof(Hold.OutlineSource),
        nameof(Hold.MetricSource),
    ];

    [Fact]
    public void Clone_CopiesEverySettableScalarProperty()
    {
        var source = new Hold();
        var properties = ClonedProperties();
        for (var i = 0; i < properties.Count; i++)
        {
            properties[i].SetValue(source, DistinctValue(properties[i], i));
        }

        var clone = source.Clone();

        Assert.NotSame(source, clone);
        foreach (var property in properties)
        {
            if (property.Name == nameof(Hold.ShapePoints))
            {
                AssertShapePointsDeepCopied(source.ShapePoints, clone.ShapePoints);
                continue;
            }

            if (property.Name == nameof(Hold.ShapeHoles))
            {
                Assert.NotSame(source.ShapeHoles, clone.ShapeHoles);
                Assert.Equal(source.ShapeHoles!.Count, clone.ShapeHoles!.Count);
                for (var r = 0; r < source.ShapeHoles.Count; r++)
                {
                    AssertShapePointsDeepCopied(source.ShapeHoles[r], clone.ShapeHoles[r]);
                }

                continue;
            }

            Assert.True(
                Equals(property.GetValue(source), property.GetValue(clone)),
                $"Hold.Clone() does not copy '{property.Name}'. Add it to Clone(), or to NotCloned if it is a navigation.");
        }
    }

    [Fact]
    public void Clone_KeepsNullGlyphMetricsNull()
    {
        var clone = new Hold().Clone();

        foreach (var name in GlyphMetricProperties)
        {
            Assert.Null(typeof(Hold).GetProperty(name)!.GetValue(clone));
        }
    }

    [Fact]
    public void CopyGlyphMetricsFrom_CopiesEveryGlyphMetric()
    {
        var source = new Hold();
        for (var i = 0; i < GlyphMetricProperties.Length; i++)
        {
            var property = typeof(Hold).GetProperty(GlyphMetricProperties[i])!;
            property.SetValue(source, DistinctValue(property, i));
        }

        var target = new Hold { X = 0.4, Y = 0.6 };
        target.CopyGlyphMetricsFrom(source);

        foreach (var name in GlyphMetricProperties)
        {
            var property = typeof(Hold).GetProperty(name)!;
            Assert.Equal(property.GetValue(source), property.GetValue(target));
        }

        // Normalized geometry is not part of the metric copy.
        Assert.Equal(0.4, target.X);
        Assert.Equal(0.6, target.Y);
    }

    private static List<PropertyInfo> ClonedProperties() =>
        typeof(Hold)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.CanWrite && p.SetMethod!.IsPublic && !NotCloned.Contains(p.Name))
            .ToList();

    /// <summary>A non-default value, distinct per property index, for every property type Hold uses.</summary>
    private static object DistinctValue(PropertyInfo property, int index)
    {
        var type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
        if (type == typeof(double))
        {
            return 1000.5 + index;
        }

        if (type == typeof(int))
        {
            return 100 + index;
        }

        if (type == typeof(bool))
        {
            return true;
        }

        if (type == typeof(Guid))
        {
            return Guid.NewGuid();
        }

        if (type == typeof(string))
        {
            return $"v{index}";
        }

        if (type.IsEnum)
        {
            var values = Enum.GetValues(type);
            return values.GetValue(values.Length - 1)!;
        }

        if (type == typeof(List<ShapePoint>))
        {
            return new List<ShapePoint> { new() { Dx = 0.1, Dy = -0.2 }, new() { Dx = 0.3, Dy = 0.4 } };
        }

        if (type == typeof(List<List<ShapePoint>>))
        {
            return new List<List<ShapePoint>>
            {
                new() { new() { Dx = 0.01, Dy = 0.02 }, new() { Dx = -0.01, Dy = 0.02 }, new() { Dx = 0, Dy = -0.01 } },
            };
        }

        throw new InvalidOperationException(
            $"Hold.{property.Name} has type {property.PropertyType}, which this test cannot populate. Teach DistinctValue about it.");
    }

    private static void AssertShapePointsDeepCopied(List<ShapePoint>? expected, List<ShapePoint>? actual)
    {
        Assert.NotNull(expected);
        Assert.NotNull(actual);
        Assert.NotSame(expected, actual);
        Assert.Equal(expected!.Count, actual!.Count);
        for (var i = 0; i < expected.Count; i++)
        {
            Assert.NotSame(expected[i], actual[i]);
            Assert.Equal(expected[i].Dx, actual[i].Dx);
            Assert.Equal(expected[i].Dy, actual[i].Dy);
        }
    }
}
