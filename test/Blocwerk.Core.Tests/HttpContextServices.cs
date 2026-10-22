// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Blocwerk.Core.Tests;

/// <summary>Gives a bare <see cref="DefaultHttpContext"/> the services an <c>IResult</c> needs to execute.</summary>
internal static class HttpContextServices
{
    public static DefaultHttpContext WithServices(this DefaultHttpContext http)
    {
        http.RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider();
        return http;
    }
}
