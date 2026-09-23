using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Configuration;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Blocwerk.Core.Tests;

/// <summary>
/// A wall with one live panel (generation 1) carrying a known mix of holds, for the outline-upgrade tests.
/// Outlining uses <see cref="EnrichmentFakes.Outlines"/>: seeds above y = 0.6 get a 40 × 60 px contour,
/// seeds below get a circle fallback. Every context is fresh (own connection).
/// </summary>
internal sealed class OutlineUpgradeScenario
{
    private OutlineUpgradeScenario(WallTestHarness harness, Guid panelId)
    {
        Harness = harness;
        PanelId = panelId;
    }

    public WallTestHarness Harness { get; }

    public Guid PanelId { get; }

    /// <summary>Auto circle, will be outlined.</summary>
    public Guid AutoOutlined { get; private set; }

    /// <summary>Auto circle whose outline falls back to the circle.</summary>
    public Guid AutoKeepsCircle { get; private set; }

    /// <summary>Auto circle that already has a fingerprint (outlined, fingerprint untouched).</summary>
    public Guid AutoWithFingerprint { get; private set; }

    /// <summary>Manual circle with a real radius (outlined only when manual holds are included).</summary>
    public Guid ManualLarge { get; private set; }

    /// <summary>Manual circle with the placeholder radius (its outline looks like a leak).</summary>
    public Guid ManualPlaceholder { get; private set; }

    /// <summary>Virtual auto hold (never touched).</summary>
    public Guid Virtual { get; private set; }

    /// <summary>Auto hold that already has a polygon (never touched).</summary>
    public Guid AlreadyShaped { get; private set; }

    /// <summary>An auto circle on a SUPERSEDED generation of the panel (not live, never touched).</summary>
    public Guid OldGeneration { get; private set; }

    public static async Task<OutlineUpgradeScenario> CreateAsync(WallTestHarness harness)
    {
        await harness.SeedWallAsync(holdCount: 0);
        var panelId = await EnrichmentScenario.AddPanelAsync(harness, livePhoto: [1, 2, 3]);
        var scenario = new OutlineUpgradeScenario(harness, panelId);
        await scenario.SeedHoldsAsync();
        return scenario;
    }

    public HoldOutlineUpgradeService Service(
        IHoldOutlineService? outlines = null, bool outlinesOn = true, IKioskContext? kiosk = null)
    {
        var settings = new BlocwerkSettings();
        settings.HoldDetection.OutlinesEnabled = outlinesOn;
        return new HoldOutlineUpgradeService(
            Harness.DbContextFactory,
            Harness.CurrentUser,
            settings,
            NullLogger<HoldOutlineUpgradeService>.Instance,
            outlines ?? EnrichmentFakes.Outlines(),
            kiosk);
    }

    public async Task<Dictionary<Guid, Hold>> LoadHoldsAsync()
    {
        await using var db = Harness.CreateContext();
        return await db.Holds.AsNoTracking().ToDictionaryAsync(h => h.Id);
    }

    private async Task SeedHoldsAsync()
    {
        await using var db = Harness.CreateContext();
        AutoOutlined = Add(db, Auto(0.2, 0.2));
        AutoKeepsCircle = Add(db, Auto(0.3, 0.7));
        var withFingerprint = Auto(0.4, 0.3);
        withFingerprint.FingerprintJson = new HoldFingerprint { L = 1, A = 2, B = 3 }.ToJson();
        AutoWithFingerprint = Add(db, withFingerprint);
        ManualLarge = Add(db, Manual(0.5, 0.2, 0.03));
        ManualPlaceholder = Add(db, Manual(0.6, 0.2, 0.003));
        var @virtual = Auto(0.7, 0.2);
        @virtual.IsVirtual = true;
        Virtual = Add(db, @virtual);
        var shaped = Auto(0.8, 0.2);
        shaped.ShapePoints = ShapePoint.DefaultOctagon(0.02);
        AlreadyShaped = Add(db, shaped);
        var old = Auto(0.9, 0.2);
        old.Generation = 0;
        OldGeneration = Add(db, old);
        await db.SaveChangesAsync();
    }

    private static Guid Add(BlocwerkDbContext db, Hold hold)
    {
        db.Holds.Add(hold);
        return hold.Id;
    }

    private Hold Auto(double x, double y)
    {
        var hold = EnrichmentFakes.AutoHold(Harness.WallId, x, y);
        hold.WallPanelId = PanelId;
        hold.Generation = 1;
        return hold;
    }

    private Hold Manual(double x, double y, double radius)
    {
        var hold = Auto(x, y);
        hold.IsAutoDetected = false;
        hold.Radius = radius;
        return hold;
    }
}
