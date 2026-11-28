// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Web.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Tests;

/// <summary>
/// <c>GET /api/v1/me/security</c>: the owner's security set-up, read-only, never a secret. Who may call at all is
/// <see cref="DeviceApiRoutePostureTests"/>; the writes stay refused in a key session (<c>ApiKeySessionGuard</c>).
/// </summary>
public sealed class MeSecurityControllerTests
{
    [Fact]
    public async Task TheOwner_ReadsWhatIsSetUp_ButNoSecret()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);
        await using (var db = h.CreateContext())
        {
            await db.Users.Where(u => u.Id == h.Owner.Id).ExecuteUpdateAsync(u => u
                .SetProperty(x => x.Email, "climber@example.test")
                .SetProperty(x => x.EmailVerified, true)
                .SetProperty(x => x.LoginUsername, "climber")
                .SetProperty(x => x.PasswordHash, "hash")
                .SetProperty(x => x.TotpEnabled, true)
                .SetProperty(x => x.TotpSecretProtected, "secret"));
        }

        var controller = new MeSecurityController(h.CurrentUser)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = DeviceKeyTestPrincipals.Device() } },
        };
        var state = Assert.IsType<DeviceSecurityState>(Assert.IsType<OkObjectResult>(await controller.Get()).Value);

        Assert.Equal(("climber@example.test", true, true, "climber", true), (state.Email, state.EmailVerified, state.HasPassword, state.LoginUsername, state.TotpEnabled));
        Assert.Equal(MeSecurityController.ManagePath, state.ManageUrl);
        var json = System.Text.Json.JsonSerializer.Serialize(state);
        Assert.DoesNotContain("hash", json, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", json, StringComparison.Ordinal);
    }
}
