// <copyright file="WallMarkersControllerTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Text.Json;
using Blocwerk.Core.Enums;
using Blocwerk.Core.MarkerPlanning;
using Blocwerk.Core.Tests.MarkerPlanning;
using Blocwerk.Web.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Blocwerk.Core.Tests.Automation;

/// <summary>
/// Preparing a marker wall over the API: the wall-admin write keys only, the marker settings' and the planner's own
/// service calls, and every write in the change journal (the wall's own row changes included).
/// </summary>
public class WallMarkersControllerTests
{
    [Theory]
    [MemberData(nameof(AutomationApiFixture.KeyNames), MemberType = typeof(AutomationApiFixture))]
    public async Task OnlyWallAdminWriteKeys_SwitchTheMarkersOn(string keyName)
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);
        var prep = new WallPrepApi(h);

        var result = await prep.Markers(AutomationApiFixture.Key(keyName, h.WallId)).SetMarkers(h.WallId, new WallMarkerSettingsRequest(true, 125));

        var admitted = AutomationApiFixture.IsWallAdminKey(keyName);
        Assert.Equal(admitted ? StatusCodes.Status200OK : StatusCodes.Status403Forbidden, AutomationApiFixture.Status(result));
        Assert.Equal(admitted, await GlyphsEnabledAsync(h));
        Assert.Equal(admitted ? 1 : 0, (await AutomationApiFixture.ApiBatchesAsync(h)).Count);
    }

    [Fact]
    public async Task AWallIsPrepared_EndToEnd_AndEveryWriteIsJournalled()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);
        var prep = new WallPrepApi(h);
        var key = ApiKeys.Wall(h.WallId);

        var set = await prep.Markers(key).SetMarkers(h.WallId, new WallMarkerSettingsRequest(true, 125));
        var save = await prep.Markers(key).SavePlan(h.WallId, PlanJson(AtticMarkerPlan.Plan));
        var saved = Assert.IsType<WallMarkerPlanSaveResponse>(Assert.IsType<OkObjectResult>(save).Value);
        var effective = await prep.Revisions(key).SetEffective(h.WallId, saved.Revision!.Value, new MarkerPlanEffectiveRequest());
        var state = Assert.IsType<WallMarkerStateResponse>(Assert.IsType<OkObjectResult>(await prep.Markers(key).State(h.WallId)).Value);

        Assert.Equal(StatusCodes.Status200OK, AutomationApiFixture.Status(set));
        Assert.Equal(StatusCodes.Status204NoContent, AutomationApiFixture.Status(effective));
        Assert.Equal((true, 125.0, 1, 1), (state.Enabled, state.MarkerSizeMm!.Value, state.CurrentRevision!.Value, state.OnWallRevision!.Value));
        var batches = await AutomationApiFixture.ApiBatchesAsync(h);
        Assert.Equal<string[]>(
            ["markers.set", "marker-plan.save", "marker-plan.effective"],
            batches.Select(b => b.Label[ApiWriteAudit.LabelPrefix.Length..].Split(' ')[0]).ToArray());
        Assert.All(batches, b => Assert.Equal((ChangeJournalScopeKind.Wall, (Guid?)h.WallId, h.Owner.Id.ToString()), (b.ScopeKind, b.ScopeId, b.Actor)));
        await using var db = h.CreateContext();
        var wallRow = await db.ChangeJournalEntries.SingleAsync(e => e.BatchId == batches[0].Id);
        Assert.Equal(("Wall", ChangeJournalOp.Update), (wallRow.EntityType, wallRow.Op));
        Assert.Contains("GlyphsEnabled", wallRow.AfterJson);
    }

    [Fact]
    public async Task RefusedWrites_ChangeNothing_AndAreNotJournalled()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);
        var prep = new WallPrepApi(h);
        var key = ApiKeys.Personal();

        var tooBig = await prep.Markers(key).SetMarkers(h.WallId, new WallMarkerSettingsRequest(true, 5000));
        var notAPlan = await prep.Markers(key).SavePlan(h.WallId, JsonDocument.Parse("{\"segments\": 3}").RootElement);
        var noRevision = await prep.Revisions(key).SetEffective(h.WallId, 7, new MarkerPlanEffectiveRequest());
        h.ActingUser = await h.AddMemberAsync("member@test", WallRole.Member);
        var member = await prep.Markers(key).SetMarkers(h.WallId, new WallMarkerSettingsRequest(true, 125));

        Assert.Equal(StatusCodes.Status400BadRequest, AutomationApiFixture.Status(tooBig));
        Assert.Equal(StatusCodes.Status422UnprocessableEntity, AutomationApiFixture.Status(notAPlan));
        Assert.Equal(StatusCodes.Status404NotFound, AutomationApiFixture.Status(noRevision));
        Assert.Equal(StatusCodes.Status403Forbidden, AutomationApiFixture.Status(member));
        Assert.False(await GlyphsEnabledAsync(h));
        Assert.Empty(await AutomationApiFixture.ApiBatchesAsync(h));
    }

    private static JsonElement PlanJson(MarkerPlan plan) => JsonDocument.Parse(MarkerPlanJson.ToJson(plan)).RootElement;

    private static async Task<bool> GlyphsEnabledAsync(WallTestHarness h)
    {
        await using var db = h.CreateContext();
        return await db.Walls.Where(w => w.Id == h.WallId).Select(w => w.GlyphsEnabled).SingleAsync();
    }
}
