// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.DeviceLinks;
using Blocwerk.Core.Services;
using Blocwerk.Web.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Blocwerk.Core.Tests.DeviceLinks;

/// <summary>The code exchange answers 403, not a server error, when the key service refuses a kiosk tablet.</summary>
public sealed class DeviceLinkTokenControllerTests
{
    [Fact]
    public async Task AKioskRefusal_Answers403()
    {
        var links = Substitute.For<IDeviceLinkService>();
        links.ExchangeAsync(Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new KioskRestrictedException("API keys cannot be created from a kiosk session."));
        var controller = new DeviceLinkTokenController(links)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };

        var result = await controller.Exchange(new DeviceLinkTokenRequest("c", "v", "d"), CancellationToken.None);

        Assert.Equal(StatusCodes.Status403Forbidden, Assert.IsType<ObjectResult>(result).StatusCode);
    }
}
