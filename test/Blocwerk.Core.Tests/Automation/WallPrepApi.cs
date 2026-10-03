// <copyright file="WallPrepApi.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Security.Claims;
using Blocwerk.Core.MarkerPlanning;
using Blocwerk.Core.Services;
using Blocwerk.Web.Controllers;
using Microsoft.Extensions.Logging.Abstractions;

namespace Blocwerk.Core.Tests.Automation;

/// <summary>The two wall-prep controllers over the real services, whose contexts journal like production's.</summary>
internal sealed class WallPrepApi
{
    private readonly ApiWriteAudit audit;
    private readonly WallGlyphService glyphs;
    private readonly MarkerPlanService plans;

    public WallPrepApi(WallTestHarness h)
    {
        var journal = AutomationApiFixture.Journal(h);
        var factory = new JournalledDbContextFactory(h, journal);
        audit = AutomationApiFixture.Audit(h, journal);
        glyphs = new WallGlyphService(factory, h.CurrentUser, NullLogger<WallGlyphService>.Instance);
        plans = new MarkerPlanService(factory, h.CurrentUser, NullLogger<MarkerPlanService>.Instance);
    }

    public WallMarkersController Markers(ClaimsPrincipal key) =>
        new WallMarkersController(glyphs, plans, audit, NullLogger<WallMarkersController>.Instance).As(key);

    public WallMarkerPlanRevisionsController Revisions(ClaimsPrincipal key) =>
        new WallMarkerPlanRevisionsController(plans, audit, NullLogger<WallMarkerPlanRevisionsController>.Instance).As(key);
}
