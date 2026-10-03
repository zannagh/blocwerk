// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Blocwerk.Core.Services.PanelCrop;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using SkiaSharp;

namespace Blocwerk.Core.Tests;

/// <summary>
/// A wall with two live panels side by side: the cropped one carries a real 400×300 JPEG, holds with outlines, one hold
/// near the right edge used by a boulder and linked to the neighbour panel, a marker observation and a pending proposal.
/// </summary>
public sealed class PanelCropFixture : IDisposable
{
    public const int PhotoWidth = 400;
    public const int PhotoHeight = 300;

    public PanelCropFixture()
    {
        Harness = new WallTestHarness();
        Queue = Substitute.For<IHoldRefinementQueue>();
        Service = new PanelCropService(Harness.DbContextFactory, Harness.CurrentUser, NullLogger<PanelCropService>.Instance, refinementQueue: Queue);
    }

    public WallTestHarness Harness { get; }

    public IHoldRefinementQueue Queue { get; }

    public PanelCropService Service { get; }

    public Guid PanelId { get; private set; }

    public byte[] OriginalPhoto { get; private set; } = [];

    /// <summary>Centre hold, well inside any crop the tests make.</summary>
    public Hold Centre { get; private set; } = null!;

    /// <summary>Hold near the right edge: cut off by a crop that keeps less than 90 % of the width.</summary>
    public Hold Edge { get; private set; } = null!;

    public Guid BoulderId { get; private set; }

    public Guid MarkerId { get; private set; }

    public Guid ProposalId { get; private set; }

    /// <summary>A solid JPEG with a gradient, so a crop is a real decode/encode.</summary>
    public static byte[] Jpeg(int width, int height)
    {
        using var bitmap = new SKBitmap(width, height);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                bitmap.SetPixel(x, y, new SKColor((byte)(x % 256), (byte)(y % 256), 128));
            }
        }

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 90);
        return data.ToArray();
    }

    /// <summary>Seeds the wall, the panels, the holds, the boulder, the link, the marker and the proposal.</summary>
    public async Task SeedAsync()
    {
        await Harness.SeedWallAsync(holdCount: 0);
        OriginalPhoto = Jpeg(PhotoWidth, PhotoHeight);
        await using var db = Harness.CreateContext();
        var panel = new WallPanel { WallId = Harness.WallId, Col = 0, Row = 0, Generation = 0, Photo = OriginalPhoto, PhotoContentType = "image/jpeg" };
        var neighbour = new WallPanel { WallId = Harness.WallId, Col = 1, Row = 0, Generation = 0, Photo = Jpeg(40, 30), PhotoContentType = "image/jpeg" };
        db.WallPanels.AddRange(panel, neighbour);
        PanelId = panel.Id;

        Centre = Hold(panel.Id, 0.5, 0.5, ShapePoint.DefaultOctagon(0.03));
        Centre.FacetId = "0";
        Centre.PlaneAMm = 1200;
        Edge = Hold(panel.Id, 0.95, 0.4, null);
        var twin = Hold(neighbour.Id, 0.05, 0.4, null);
        db.Holds.AddRange(Centre, Edge, twin);
        db.HoldLinks.Add(new HoldLink { WallId = Harness.WallId, HoldAId = Edge.Id, HoldBId = twin.Id });

        var boulder = new Boulder { WallId = Harness.WallId, Name = "Edge Problem", Grade = "6a", CreatedByUserId = Harness.Owner.Id };
        db.Boulders.Add(boulder);
        db.BoulderHolds.Add(new BoulderHold { BoulderId = boulder.Id, HoldId = Edge.Id });
        BoulderId = boulder.Id;

        var marker = new WallMarkerObservation { WallPanelId = panel.Id, MarkerId = 3, CornersJson = "[[0.4,0.4],[0.6,0.4],[0.6,0.6],[0.4,0.6]]", SidePx = 80 };
        db.WallMarkerObservations.Add(marker);
        MarkerId = marker.Id;
        ProposalId = AddProposal(db, panel.Id);
        await db.SaveChangesAsync();
    }

    public async Task<Hold> LoadHoldAsync(Guid id)
    {
        await using var db = Harness.CreateContext();
        return await db.Holds.AsNoTracking().SingleAsync(h => h.Id == id);
    }

    public async Task<WallPanel> LoadPanelAsync()
    {
        await using var db = Harness.CreateContext();
        return await db.WallPanels.AsNoTracking().SingleAsync(p => p.Id == PanelId);
    }

    public void Dispose() => Harness.Dispose();

    private Hold Hold(Guid panelId, double x, double y, List<ShapePoint>? shape) => new()
    {
        WallId = Harness.WallId, WallPanelId = panelId, X = x, Y = y, Radius = 0.03, ShapePoints = shape, Generation = 0,
    };

    private Guid AddProposal(Blocwerk.Core.Data.BlocwerkDbContext db, Guid panelId)
    {
        var proposal = new HoldProposal
        {
            WallId = Harness.WallId, GeometryModelId = Guid.NewGuid(), FacetId = "0", BestPhoto = "cam0",
            PanelId = panelId, PanelX = 0.3, PanelY = 0.6, PanelRadius = 0.02, Status = HoldProposalStatus.Pending,
        };
        db.HoldProposals.Add(proposal);
        return proposal.Id;
    }
}
