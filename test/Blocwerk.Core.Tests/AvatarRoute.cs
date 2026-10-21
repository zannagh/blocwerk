// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Data;
using Blocwerk.Web.Endpoints;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Blocwerk.Core.Tests;

/// <summary>Calls the real registered <c>/api/users/{u}/avatar</c> endpoint in-process against a harness database.</summary>
public static class AvatarRoute
{
    public static async Task<DefaultHttpContext> GetAsync(WallTestHarness harness, Guid userId)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddSingleton<IDbContextFactory<BlocwerkDbContext>>(harness.DbContextFactory);
        await using var app = builder.Build();
        app.MapUserAvatars();
        var endpoint = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(s => s.Endpoints).OfType<RouteEndpoint>().Single(e => e.RoutePattern.RawText == "/api/users/{userId:guid}/avatar");

        var http = new DefaultHttpContext { RequestServices = app.Services };
        http.Request.Method = HttpMethods.Get;
        http.Request.RouteValues["userId"] = userId.ToString();
        http.Response.Body = new MemoryStream();
        await endpoint.RequestDelegate!(http);
        return http;
    }
}
