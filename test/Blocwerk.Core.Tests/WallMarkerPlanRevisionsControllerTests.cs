// <copyright file="WallMarkerPlanRevisionsControllerTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Security.Claims;
using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Entities;
using Blocwerk.Core.MarkerPlanning;
using Blocwerk.Core.Services;
using Blocwerk.Web.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Blocwerk.Core.Tests;

/// <summary>
/// "Markers swapped on the wall" over the machine API is a wall-admin write: a wall key needs write access for it,
/// like every other wall-admin route, and no device flow uses it.
/// </summary>
public class WallMarkerPlanRevisionsControllerTests
{
    private static readonly Guid WallId = Guid.NewGuid();

    [Fact]
    public async Task WallKeyWithoutWriteAccess_CannotMarkARevisionEffective()
    {
        var plans = Substitute.For<IMarkerPlanService>();

        var result = await Api(plans, ApiKeys.Wall(WallId, allowWrite: false)).SetEffective(WallId, 1, new MarkerPlanEffectiveRequest());

        Assert.Equal(StatusCodes.Status403Forbidden, Assert.IsType<ObjectResult>(result).StatusCode);
        await plans.DidNotReceiveWithAnyArgs().SetRevisionEffectiveAsync(default, default, default);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task WriteKeys_MarkARevisionEffective(bool wallKey)
    {
        var plans = Substitute.For<IMarkerPlanService>();
        plans.SetRevisionEffectiveAsync(WallId, 1, Arg.Any<DateTimeOffset?>()).Returns(true);
        var key = wallKey ? ApiKeys.Wall(WallId) : ApiKeys.Personal();

        var result = await Api(plans, key).SetEffective(WallId, 1, new MarkerPlanEffectiveRequest());

        Assert.IsType<NoContentResult>(result);
    }

    private static WallMarkerPlanRevisionsController Api(IMarkerPlanService plans, ClaimsPrincipal key) =>
        new(plans, Audit(), NullLogger<WallMarkerPlanRevisionsController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = key } },
        };

    /// <summary>A real audit over a throwaway database: these tests are about the key guard.</summary>
    private static ApiWriteAudit Audit()
    {
        var factory = new TestDbContextFactory(TestDbContextFactory.IsolatedDatabase());
        var keepAlive = factory.CreateDbContext();
        keepAlive.Database.OpenConnection();
        keepAlive.Database.EnsureCreated();
        var users = Substitute.For<ICurrentUserService>();
        users.GetCurrentUserAsync().Returns(new User { Identifier = "owner@test" });
        return new ApiWriteAudit(new ChangeJournal(factory.CreateDbContext), users, NullLogger<ApiWriteAudit>.Instance);
    }
}
