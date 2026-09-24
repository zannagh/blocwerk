// <copyright file="WallCapturesControllerTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Reflection;
using Blocwerk.Authentication.Authorization;
using Blocwerk.Authentication.Handlers;
using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Capture;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Web.Controllers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The capture API against the real capture service: a personal key with write access of a wall admin runs the whole
/// flow (draft, streamed multipart photos, declarations, start, status); the same key without write access, a
/// member's key, a kiosk session and a wall key of another wall are refused; a capture is only served under its wall.
/// </summary>
public class WallCapturesControllerTests
{
    [Fact]
    public void Route_IsWallOrPersonalKeyOnly()
    {
        var authorize = typeof(WallCapturesController).GetCustomAttribute<AuthorizeAttribute>();

        Assert.Equal(BlocwerkPolicies.AnyApiKey, authorize!.Policy);
        Assert.Equal(ApiKeyAuthenticationHandler.SchemeName, authorize.AuthenticationSchemes);
        Assert.Equal("api/walls/{wallId:guid}/captures", typeof(WallCapturesController).GetCustomAttribute<RouteAttribute>()!.Template);
        Assert.True(typeof(WallScopedApiController).IsAssignableFrom(typeof(WallCapturesController)));
    }

    [Fact]
    public async Task PersonalKeyWithWriteAccess_OfAnAdmin_RunsTheWholeFlow()
    {
        using var h = new WallTestHarness();
        using var s = await GlyphWallAsync(h);
        var api = Api(s, ApiKeys.Personal());

        var draft = Body<WallCaptureDraft>(await api.CreateDraft(h.WallId));
        ApiKeys.Multipart(api, ("IMG_0.jpg", Photo(0)), ("IMG_1.jpg", Photo(1)), ("IMG_0.jpg", Photo(0)));
        var upload = Body<CapturePhotoUploadResponse>(await api.UploadPhotos(h.WallId, draft.CaptureId, default));
        var declarations = Body<CaptureDeclarationsResponse>(await api.Declarations(h.WallId, draft.CaptureId));
        var segments = declarations.Declarations.Segments
            .Select(x => x with { Name = x.Index == 1 ? "Cave" : x.Name, DeclaredAngleDeg = x.Index == 0 ? 30 : null, VerticalReference = false })
            .ToList();
        var started = Assert.IsType<AcceptedResult>(await api.Start(
            h.WallId, draft.CaptureId, new CaptureStartRequest(segments, [[14, 15]], "from the API", SplatQuality.Draft)));
        var status = Body<WallCaptureSummary>(await api.Get(h.WallId, draft.CaptureId));

        Assert.Equal((2, 1), (upload.Stored, upload.Refused));
        Assert.Contains("already uploaded", upload.Items[2].Error);
        var answer = Assert.IsType<CaptureStartResponse>(started.Value);
        Assert.Contains(answer.Warnings, w => w.StartsWith("Segment 1 (“Cave”) has no angle", StringComparison.Ordinal));
        Assert.Equal(WallCaptureStatus.Queued, status.Status);
        Assert.Equal(h.WallId, status.WallId);
        Assert.Equal(draft.CaptureId, await s.Queue.DequeueAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token));
        await using var db = h.CreateContext();
        Assert.Equal(SplatQuality.Draft, (await db.WallCaptures.SingleAsync()).SplatQuality);
    }

    [Fact]
    public async Task Start_WithoutADeclaration_UsesTheSuggestion_AndReportsProblemsAs422()
    {
        using var h = new WallTestHarness();
        using var s = await GlyphWallAsync(h);
        var api = Api(s, ApiKeys.Personal());
        var draft = Body<WallCaptureDraft>(await api.CreateDraft(h.WallId));

        var tooFew = await api.Start(h.WallId, draft.CaptureId, null);

        var problems = Assert.IsType<CaptureStartProblems>(Assert.IsType<UnprocessableEntityObjectResult>(tooFew).Value);
        Assert.Contains("Upload at least two photos of the wall.", problems.Problems);
    }

    [Fact]
    public async Task PersonalKeyWithoutWriteAccess_IsForbidden_EvenForAnAdmin()
    {
        using var h = new WallTestHarness();
        using var s = await GlyphWallAsync(h);
        var api = Api(s, ApiKeys.Personal(allowWrite: false));

        Assert.Equal(StatusCodes.Status403Forbidden, Status(await api.CreateDraft(h.WallId)));
        Assert.Equal(StatusCodes.Status403Forbidden, Status(await api.List(h.WallId)));
        await AssertNoCaptureAsync(h);
    }

    [Fact]
    public async Task PersonalKeyOfAMember_IsForbidden_ByTheSameWallAdminCheckAsTheBrowser()
    {
        using var h = new WallTestHarness();
        using var s = await GlyphWallAsync(h);
        var draft = await s.Service.CreateDraftAsync(h.WallId);
        h.ActingUser = await h.AddMemberAsync("member@test", WallRole.Member);
        var api = Api(s, ApiKeys.Personal());

        Assert.Equal(StatusCodes.Status403Forbidden, Status(await api.CreateDraft(h.WallId)));
        Assert.Equal(StatusCodes.Status403Forbidden, Status(await api.Get(h.WallId, draft.CaptureId)));
        Assert.Equal(StatusCodes.Status403Forbidden, Status(await api.Start(h.WallId, draft.CaptureId, null)));
    }

    [Fact]
    public async Task AKioskSession_IsRefused()
    {
        using var h = new WallTestHarness();
        var kiosk = Substitute.For<IKioskContext>();
        kiosk.IsKiosk.Returns(true);
        kiosk.KioskWallId.Returns(_ => h.WallId);
        using var s = await GlyphWallAsync(h, kiosk);
        var api = Api(s, ApiKeys.Personal());

        Assert.Equal(StatusCodes.Status403Forbidden, Status(await api.CreateDraft(h.WallId)));
        await AssertNoCaptureAsync(h);
    }

    [Fact]
    public async Task WallKeys_WorkOnTheirOwnWallOnly_AndACaptureOnlyUnderItsWall()
    {
        using var h = new WallTestHarness();
        using var s = await GlyphWallAsync(h);
        var draft = await s.Service.CreateDraftAsync(h.WallId);
        var otherWall = Guid.NewGuid();

        Assert.Equal(StatusCodes.Status403Forbidden, Status(await Api(s, ApiKeys.Wall(otherWall)).CreateDraft(h.WallId)));
        Assert.IsType<OkObjectResult>(await Api(s, ApiKeys.Wall(h.WallId)).Get(h.WallId, draft.CaptureId));
        Assert.IsType<NotFoundObjectResult>(await Api(s, ApiKeys.Personal()).Get(otherWall, draft.CaptureId));
        Assert.IsType<NotFoundObjectResult>(await Api(s, ApiKeys.Personal()).Get(h.WallId, Guid.NewGuid()));
    }

    [Fact]
    public async Task Status_CarriesTheSolversModelChecks_AsAModelChecksArray()
    {
        using var h = new WallTestHarness();
        using var s = await GlyphWallAsync(h);
        var draft = await s.Service.CreateDraftAsync(h.WallId);
        Guid solvedId;
        await using (var db = h.CreateContext())
        {
            var model = new WallGeometryModel
            {
                WallId = h.WallId, Json = WallGeometryModelChecksTests.WithChecks(withWarnings: true), SchemaVersion = 1, Source = "test",
            };
            var solved = new WallCapture
            {
                WallId = h.WallId, CreatedByUserId = h.Owner.Id, Status = WallCaptureStatus.Succeeded, GeometryModelId = model.Id,
            };
            db.AddRange(model, solved);
            await db.SaveChangesAsync();
            solvedId = solved.Id;
        }

        var api = Api(s, ApiKeys.Personal());
        var status = Body<WallCaptureSummary>(await api.Get(h.WallId, solvedId));
        var open = Body<WallCaptureSummary>(await api.Get(h.WallId, draft.CaptureId));
        var json = System.Text.Json.JsonSerializer.Serialize(status, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));

        Assert.Contains(status.ModelChecks!, c => c.Kind == Geometry.WallGeometryModelCheck.KindSolverWarning && c.Level == "warning");
        Assert.Contains(status.ModelChecks!, c => c.Message == "Main wall 44.6° overhang (declared 45°)");
        Assert.Empty(open.ModelChecks!);
        Assert.Contains("\"modelChecks\":[{\"kind\":\"segment-angle\",\"level\":\"info\",", json);
    }

    private static async Task<CaptureScenario> GlyphWallAsync(WallTestHarness h, IKioskContext? kiosk = null)
    {
        var s = new CaptureScenario(h, kiosk: kiosk);
        await h.SeedWallAsync(holdCount: 0);
        await using var db = h.CreateContext();
        (await db.Walls.SingleAsync()).GlyphsEnabled = true;
        await db.SaveChangesAsync();
        return s;
    }

    private static WallCapturesController Api(CaptureScenario s, System.Security.Claims.ClaimsPrincipal key)
    {
        var controller = new WallCapturesController(s.Service, s.Options, NullLogger<WallCapturesController>.Instance);
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = key } };
        return controller;
    }

    private static byte[] Photo(int seed) => ExifJpeg.Build(CaptureScenario.TinyJpeg(seed));

    private static T Body<T>(IActionResult result) => Assert.IsType<T>(Assert.IsType<OkObjectResult>(result).Value);

    private static int? Status(IActionResult result) => Assert.IsAssignableFrom<ObjectResult>(result).StatusCode;

    private static async Task AssertNoCaptureAsync(WallTestHarness h)
    {
        await using var db = h.CreateContext();
        Assert.False(await db.WallCaptures.AnyAsync());
    }
}
