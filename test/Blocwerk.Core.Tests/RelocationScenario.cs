// <copyright file="RelocationScenario.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Blocwerk.Core.Tests;

/// <summary>
/// A staged centre update in which ONE hold physically moved. Live gen 2: A (stays put) and M (moved away),
/// both on boulder "Route". Staged gen 3: A' (A's twin, same spot), M' (M at its new spot) and N' (a genuinely
/// new, differently coloured hold). The matcher stub pairs holds by position, so A↔A' is matched while M is
/// "disappeared" and M', N' are "appeared" — exactly the situation the relocation suggestions exist for.
/// </summary>
internal sealed class RelocationScenario
{
    private RelocationScenario(WallTestHarness h)
    {
        Harness = h;
    }

    public WallTestHarness Harness { get; }

    public Guid WallId { get; private set; }

    public Guid SessionId { get; private set; }

    public Guid OldA { get; private set; }

    public Guid OldM { get; private set; }

    public Guid NewA { get; private set; }

    public Guid NewM { get; private set; }

    public Guid NewN { get; private set; }

    public Guid BoulderId { get; private set; }

    /// <summary>A green, blob-shaped fingerprint without millimetres.</summary>
    public static HoldFingerprint Green(double? widthMm = null) => new()
    {
        L = 140, A = 90, B = 160, AreaPx = 900, Aspect = 1.3, Solidity = 0.92,
        Histogram = Hist(3), Hu = [0.8, 2.9, 3.6, 3.9, 7.6, 5.4, 7.7],
        WidthMm = widthMm, HeightMm = widthMm is { } w ? w * 0.7 : null, AreaMm2 = widthMm is { } a ? a * a * 0.6 : null,
    };

    /// <summary>A red, elongated fingerprint — nothing like <see cref="Green"/>.</summary>
    public static HoldFingerprint Red() => new()
    {
        L = 120, A = 180, B = 150, AreaPx = 700, Aspect = 2.6, Solidity = 0.70,
        Histogram = Hist(0), Hu = [1.4, 3.8, 4.9, 5.2, 10.1, 7.2, 10.4],
    };

    public static double[] Hist(int bin)
    {
        var h = new double[HoldFingerprint.HueBins + 1];
        h[bin] = 1;
        return h;
    }

    /// <summary>Seeds the scenario; <paramref name="fingerprints"/> false leaves every hold without one.</summary>
    public static async Task<RelocationScenario> SeedAsync(WallTestHarness h, bool fingerprints = true, bool glyphs = false)
    {
        var s = new RelocationScenario(h);
        await using var db = h.CreateContext();
        db.Users.Add(h.Owner);
        var wall = new Wall
        {
            Name = "Moves", OwnerId = h.Owner.Id, CurrentGeneration = 2, GlyphsEnabled = glyphs,
            Photo = [1, 2, 3], PhotoContentType = "image/jpeg", UsesMultipleImages = true,
        };
        db.Walls.Add(wall);
        db.WallMembers.Add(new WallMember { WallId = wall.Id, UserId = h.Owner.Id, Role = WallRole.Admin });
        var live = new WallPanel { WallId = wall.Id, Col = 0, Row = 0, Photo = [1], PhotoContentType = "image/jpeg", Generation = 2 };
        var staged = new WallPanel
        {
            WallId = wall.Id, Col = 0, Row = 0, StagedPhoto = [7], StagedPhotoContentType = "image/jpeg", Generation = 3,
        };
        db.WallPanels.AddRange(live, staged);

        string? Fp(HoldFingerprint f) => fingerprints ? f.ToJson() : null;
        var a = Old(wall.Id, live.Id, 0.2, 0.2, Fp(Green()));
        var m = Old(wall.Id, live.Id, 0.5, 0.5, Fp(Green()));
        m.Name = "Green blob";
        m.Color = "#2ecc71";
        m.Category = HoldCategory.Foot;
        m.Material = HoldMaterial.Wood;
        m.HandType = HoldHandType.Sloper;
        var newA = Staged(wall.Id, staged.Id, 0.2, 0.2, Fp(Green()));
        var newM = Staged(wall.Id, staged.Id, 0.8, 0.3, Fp(Green()));
        var newN = Staged(wall.Id, staged.Id, 0.3, 0.8, Fp(Red()));
        db.Holds.AddRange(a, m, newA, newM, newN);

        var boulder = new Boulder { WallId = wall.Id, Name = "Route", CreatedByUserId = h.Owner.Id, Generation = 2 };
        db.Boulders.Add(boulder);
        db.BoulderHolds.Add(new BoulderHold { BoulderId = boulder.Id, HoldId = a.Id });
        db.BoulderHolds.Add(new BoulderHold { BoulderId = boulder.Id, HoldId = m.Id });

        var session = WallUpdateSessions.Open(db, wall.Id, 3, h.Owner.Id);
        await db.SaveChangesAsync();

        (s.WallId, s.SessionId, s.BoulderId) = (wall.Id, session.Id, boulder.Id);
        (s.OldA, s.OldM, s.NewA, s.NewM, s.NewN) = (a.Id, m.Id, newA.Id, newM.Id, newN.Id);
        return s;
    }

    public WallBigUpdateService BigUpdate() =>
        new(
            Harness.DbContextFactory,
            Harness.CurrentUser,
            Harness.HoldDetection,
            new PositionHoldMatcher(),
            NullLogger<WallBigUpdateService>.Instance);

    public WallUpdateSessionService Sessions() => WallUpdateSessionFixture.Sessions(Harness);

    /// <summary>
    /// What the review's Continue sends: the carry-all seed (A on its matched twin, M carried blind) with
    /// the session's recorded verdicts laid over it, and every staged hold no verdict consumed kept as new.
    /// </summary>
    public async Task ContinueAsync()
    {
        var sessions = Sessions();
        var decisions = new Dictionary<Guid, CarryoverDecision>
        {
            [OldA] = new(OldA, CarryKind.Carried, NewA),
            [OldM] = new(OldM, CarryKind.Carried, null),
        };
        foreach (var d in (await sessions.GetDecisionsAsync(WallId)).Carryover)
        {
            decisions[d.OldHoldId] = d;
        }

        var consumed = decisions.Values.Where(d => d.NewHoldId is not null).Select(d => d.NewHoldId!.Value).ToHashSet();
        var accepted = new[] { NewA, NewM, NewN }.Where(id => !consumed.Contains(id)).ToList();
        await sessions.SaveCarryOutcomeAsync(WallId, decisions.Values.ToList(), accepted, []);
    }

    /// <summary>Promotes exactly what the session recorded, as the wizard does.</summary>
    public async Task PromoteAsync()
    {
        var confirmation = await Sessions().GetDecisionsAsync(WallId);
        await BigUpdate().PromoteAsync(WallId, confirmation, SessionId);
    }

    private static Hold Old(Guid wallId, Guid panelId, double x, double y, string? fp) => new()
    {
        WallId = wallId, WallPanelId = panelId, X = x, Y = y, Radius = 0.02, Generation = 2, FingerprintJson = fp,
    };

    private static Hold Staged(Guid wallId, Guid panelId, double x, double y, string? fp) => new()
    {
        WallId = wallId, WallPanelId = panelId, X = x, Y = y, Radius = 0.02, Generation = 3,
        IsAutoDetected = true, NeedsReview = true, FingerprintJson = fp,
    };
}
