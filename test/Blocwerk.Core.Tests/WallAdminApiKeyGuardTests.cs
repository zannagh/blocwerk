// <copyright file="WallAdminApiKeyGuardTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Reflection;
using System.Security.Claims;
using Blocwerk.Authentication.Authorization;
using Blocwerk.Web.Controllers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The guard every wall-admin machine route runs (captures, coverage, hold proposals, volumes, corrections, hold
/// placement, hold shapes, the wall update's shape step). Wall keys live on devices bolted to walls and must be
/// assumed to leak, so a wall key gets in only when its owner created it with write access — exactly like a
/// personal key — and only on its own wall. Kiosk keys never get in.
/// </summary>
public class WallAdminApiKeyGuardTests
{
    private static readonly Guid WallId = Guid.NewGuid();

    public static TheoryData<string, ClaimsPrincipal, bool> Keys() => new()
    {
        { "personal key with write access", ApiKeys.Personal(), true },
        { "read-only personal key", ApiKeys.Personal(allowWrite: false), false },
        { "wall key with write access", ApiKeys.Wall(WallId), true },
        { "read-only wall key", ApiKeys.Wall(WallId, allowWrite: false), false },
        { "wall key with write access of another wall", ApiKeys.Wall(Guid.NewGuid()), false },
        { "kiosk key", ApiKeys.Kiosk(WallId), false },
        { "installation key", ApiKeys.Installation(), false },
    };

    [Theory]
    [MemberData(nameof(Keys))]
    public void Guard_AdmitsOnlyWriteKeys_AndWallKeysOnlyOnTheirWall(string name, ClaimsPrincipal key, bool admitted)
    {
        var result = new WallScopedApiControllerProbe(key).Guard(WallId);

        if (admitted)
        {
            Assert.True(result is null, name);
        }
        else
        {
            Assert.Equal(StatusCodes.Status403Forbidden, Assert.IsType<ObjectResult>(result).StatusCode);
        }
    }

    /// <summary>
    /// Every controller that admits wall keys alongside personal keys is a wall-admin controller and so must run its
    /// actions through <see cref="WallAdminApiController"/>'s RunAsync, where the device guard is hidden behind a
    /// compile error. Every other wall-scoped controller is a device controller and admits wall keys only.
    /// </summary>
    [Fact]
    public void AnyApiKeyControllers_AreWallAdminControllers_AndTheRestAreDeviceOnly()
    {
        var types = typeof(WallScopedApiController).Assembly.GetTypes();
        var wallScoped = types.Where(t => !t.IsAbstract && typeof(WallScopedApiController).IsAssignableFrom(t)).ToList();
        var anyKey = types.Where(t => t.GetCustomAttribute<AuthorizeAttribute>()?.Policy == BlocwerkPolicies.AnyApiKey).ToList();

        Assert.NotEmpty(anyKey);
        Assert.All(anyKey, t => Assert.True(typeof(WallAdminApiController).IsAssignableFrom(t), t.Name));
        Assert.All(
            wallScoped.Where(t => !typeof(WallAdminApiController).IsAssignableFrom(t)),
            t => Assert.Equal(BlocwerkPolicies.WallApiKey, t.GetCustomAttribute<AuthorizeAttribute>()?.Policy));
    }

    [Fact]
    public void TheDeviceGuard_IsACompileErrorInWallAdminControllers()
    {
        var hidden = typeof(WallAdminApiController).GetMethod(
            "GuardWall", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);

        Assert.NotNull(hidden);
        Assert.True(hidden!.GetCustomAttribute<ObsoleteAttribute>()?.IsError);
    }
}
