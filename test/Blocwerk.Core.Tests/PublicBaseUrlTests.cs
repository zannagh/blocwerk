// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Configuration;
using Blocwerk.Web.Controllers;
using Microsoft.AspNetCore.Http;

namespace Blocwerk.Core.Tests;

/// <summary>
/// <see cref="PublicBaseUrl"/>: a configured public server address wins over the request's host; the localhost default
/// or a missing address falls back to the request, as the web pages do.
/// </summary>
public sealed class PublicBaseUrlTests
{
    [Theory]
    [InlineData("https://blocwerk.app", "https://blocwerk.app")]
    [InlineData("https://blocwerk.app/", "https://blocwerk.app")]
    [InlineData("https://example.org/blocwerk/", "https://example.org/blocwerk")]
    [InlineData("https://localhost:5001", "https://internal:8080")]
    [InlineData("http://127.0.0.1:5000", "https://internal:8080")]
    [InlineData("", "https://internal:8080")]
    [InlineData("not a url", "https://internal:8080")]
    public void TheOrigin_IsTheConfiguredAddressUnlessItIsLocal(string configured, string expected)
    {
        var settings = new BlocwerkSettings();
        settings.Server.Url = configured;
        var request = new DefaultHttpContext().Request;
        request.Scheme = "https";
        request.Host = new HostString("internal:8080");

        Assert.Equal(expected, PublicBaseUrl.Of(settings, request));
    }
}
