// <copyright file="HoldShapeMarkupTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Entities;
using Blocwerk.Web.Components.Shared;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

namespace Blocwerk.Core.Tests;

/// <summary>
/// Pocket holds draw an evenodd path (holes cut out, no pointer events) under a transparent hit polygon over
/// the FULL outer ring; solid holds must render byte-for-byte as before pocket support.
/// </summary>
public class HoldShapeMarkupTests
{
    // Captured from HoldShape.razor BEFORE pocket support existed (same parameters as below).
    private const string BaselinePolygon =
        "<polygon points=\"39.00,29.00 41.20,29.20 40.90,31.10 38.90,30.70\" fill=\"rgba(255,0,0,0.2)\" stroke=\"#f00\" "
        + "stroke-width=\"0.35\" stroke-dasharray=\"0.6 0.4\" pointer-events=\"all\" style=\"cursor: pointer\" "
        + "__internal_stopPropagation_onclick __internal_stopPropagation_onpointerdown></polygon>";

    private const string BaselineCircle =
        "<circle cx=\"40.00\" cy=\"30.00\" r=\"2.00\" fill=\"none\" stroke=\"#0f0\" stroke-width=\"0.3\" __internal_stopPropagation_onclick></circle>";

    private const string BaselineFoot =
        "<rect x=\"38.60\" y=\"28.60\" width=\"2.80\" height=\"2.80\" rx=\"0.3\" fill=\"none\" stroke=\"#00f\" stroke-width=\"0.3\" "
        + "style=\"cursor: inherit; pointer-events: none;\"></rect>";

    private static readonly List<ShapePoint> Outer =
    [
        new() { Dx = -0.01, Dy = -0.01 }, new() { Dx = 0.012, Dy = -0.008 },
        new() { Dx = 0.009, Dy = 0.011 }, new() { Dx = -0.011, Dy = 0.007 },
    ];

    private static readonly List<List<ShapePoint>> Hole =
    [
        [new() { Dx = -0.003, Dy = -0.002 }, new() { Dx = 0.003, Dy = -0.002 }, new() { Dx = 0, Dy = 0.003 }],
    ];

    [Fact]
    public void SolidHolds_RenderExactlyAsBeforePocketSupport()
    {
        Assert.Equal(BaselinePolygon, HoldShapeRenderer.Render(PolygonParams(holes: null)));
        Assert.Equal(BaselinePolygon, HoldShapeRenderer.Render(PolygonParams(holes: new List<List<ShapePoint>>())));
        Assert.Equal(BaselineCircle, HoldShapeRenderer.Render(new()
        {
            ["X"] = 0.4, ["Y"] = 0.3, ["Radius"] = 0.02, ["Fill"] = "none", ["Stroke"] = "#0f0", ["StrokeWidth"] = "0.3", ["OnClick"] = Click(),
        }));
        Assert.Equal(BaselineFoot, HoldShapeRenderer.Render(new()
        {
            ["X"] = 0.4, ["Y"] = 0.3, ["Radius"] = 0.02, ["Foot"] = true, ["Fill"] = "none", ["Stroke"] = "#00f",
            ["StrokeWidth"] = "0.3", ["Style"] = "cursor: inherit; pointer-events: none;",
        }));
    }

    [Fact]
    public void HolesWithoutAnOutline_AreIgnored()
    {
        var html = HoldShapeRenderer.Render(new()
        {
            ["X"] = 0.4, ["Y"] = 0.3, ["Radius"] = 0.02, ["ShapeHoles"] = Hole, ["Fill"] = "none", ["Stroke"] = "#0f0",
            ["StrokeWidth"] = "0.3", ["OnClick"] = Click(),
        });

        Assert.Equal(BaselineCircle, html);
    }

    [Fact]
    public void PocketHold_DrawsEvenOddPath_UnderAFullOuterHitPolygon()
    {
        var html = HoldShapeRenderer.Render(PolygonParams(Hole));

        Assert.Equal(
            "<path class=\"hold-pocket\" d=\"M39.00,29.00 L41.20,29.20 L40.90,31.10 L38.90,30.70 Z M39.70,29.80 L40.30,29.80 L40.00,30.30 Z\" "
            + "fill-rule=\"evenodd\" fill=\"rgba(255,0,0,0.2)\" stroke=\"#f00\" stroke-width=\"0.35\" stroke-dasharray=\"0.6 0.4\" pointer-events=\"none\"></path>\n    "
            + "<polygon class=\"hold-hit\" points=\"39.00,29.00 41.20,29.20 40.90,31.10 38.90,30.70\" fill=\"transparent\" stroke=\"transparent\" "
            + "stroke-width=\"0.35\" stroke-dasharray=\"0.6 0.4\" pointer-events=\"all\" style=\"cursor: pointer\" "
            + "__internal_stopPropagation_onclick __internal_stopPropagation_onpointerdown></polygon>",
            html);
    }

    [Fact]
    public void MarkupHelper_PathHasHoleRings_HitPolygonHasNone()
    {
        string path = HoldShapeMarkup.PocketPath(0.4, 0.3, Outer, Hole);
        string hit = HoldShapeMarkup.PolygonPoints(0.4, 0.3, Outer);

        Assert.Equal(2, path.Split('M').Length - 1);
        Assert.Contains("M39.70,29.80 L40.30,29.80 L40.00,30.30 Z", path);
        Assert.Equal("39.00,29.00 41.20,29.20 40.90,31.10 38.90,30.70", hit);
        Assert.DoesNotContain("39.70,29.80", hit);
    }

    [Fact]
    public void DegenerateHoleRings_AreSkipped_AndAloneDoNotMakeAPocket()
    {
        var degenerate = new List<List<ShapePoint>> { new() { new() { Dx = 0, Dy = 0 }, new() { Dx = 0.001, Dy = 0 } } };

        Assert.False(HoldShapeMarkup.HasPocket(Outer, degenerate));
        Assert.False(HoldShapeMarkup.HasPocket(null, Hole));
        Assert.True(HoldShapeMarkup.HasPocket(Outer, Hole));
        Assert.Equal(1, HoldShapeMarkup.PocketPath(0.4, 0.3, Outer, [.. Hole, .. degenerate]).Split('M').Length - 2);
    }

    private static Dictionary<string, object?> PolygonParams(List<List<ShapePoint>>? holes) => new()
    {
        ["X"] = 0.4, ["Y"] = 0.3, ["Radius"] = 0.02, ["ShapePoints"] = Outer, ["ShapeHoles"] = holes,
        ["Fill"] = "rgba(255,0,0,0.2)", ["Stroke"] = "#f00", ["StrokeWidth"] = "0.35", ["Dash"] = "0.6 0.4",
        ["PointerEvents"] = "all", ["Style"] = "cursor: pointer", ["OnClick"] = Click(), ["OnPointerDown"] = Down(),
    };

    private static EventCallback<MouseEventArgs> Click() => EventCallback.Factory.Create<MouseEventArgs>(new object(), () => { });

    private static EventCallback<PointerEventArgs> Down() => EventCallback.Factory.Create<PointerEventArgs>(new object(), () => { });
}
