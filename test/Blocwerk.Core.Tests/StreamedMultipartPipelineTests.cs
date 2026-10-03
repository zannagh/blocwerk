// <copyright file="StreamedMultipartPipelineTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Blocwerk.Authentication.Authorization;
using Blocwerk.Authentication.Handlers;
using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Capture;
using Blocwerk.Core.Services;
using Blocwerk.Web.Controllers;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The streamed multipart API actions through the FULL MVC pipeline on a real Kestrel socket, which the direct
/// controller tests skip: model binding runs every value provider, and the form providers used to read (and
/// consume) a multipart body before the action's own MultipartReader got to it — every photo upload was a 500
/// "Unexpected end of Stream".
/// </summary>
public class StreamedMultipartPipelineTests
{
    [Fact]
    public async Task CapturePhotoUpload_StreamsEveryFilePart()
    {
        var wallId = Guid.NewGuid();
        var captureId = Guid.NewGuid();
        var received = new List<(string? Name, int Length)>();
        var captures = Substitute.For<IWallCaptureService>();
        captures.GetCaptureAsync(captureId).Returns(new WallCaptureSummary(
            captureId, DateTimeOffset.UtcNow, default, 0, null, null, null, 0, null, null, WallId: wallId));
        captures.AddPhotoAsync(captureId, Arg.Any<string?>(), Arg.Any<byte[]>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                received.Add((call.ArgAt<string?>(1), call.ArgAt<byte[]>(2).Length));
                return new CapturePhotoResult(Guid.NewGuid(), received.Count - 1, call.ArgAt<string?>(1), 1, 1, null, [], []);
            });

        await using var app = await StartAsync(s => s.AddSingleton(captures));
        using var client = Client(app);
        using var body = Multipart(("IMG_0.jpg", "image/jpeg", 300_000), ("IMG_1.jpg", "image/jpeg", 5));

        var response = await client.PostAsync($"api/walls/{wallId}/captures/{captureId}/photos", body);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var upload = await response.Content.ReadFromJsonAsync<CapturePhotoUploadResponse>();
        Assert.Equal((2, 0), (upload!.Stored, upload.Refused));
        Assert.Equal([("IMG_0.jpg", 300_000), ("IMG_1.jpg", 5)], received);
    }

    [Fact]
    public async Task WallImageUpload_ReadsTheMultipartSectionsItself()
    {
        var wallId = Guid.NewGuid();
        await using var app = await StartAsync(s => s
            .AddSingleton(Substitute.For<IWallImageService>())
            .AddSingleton(Substitute.For<IWallImageStorage>())
            .AddSingleton(Substitute.For<ICurrentUserService>()));
        using var client = Client(app);
        client.DefaultRequestHeaders.Add(ApiKeyPrincipalAuthHandler.WallHeader, wallId.ToString());
        using var body = Multipart(("notes.txt", "text/plain", 10));

        var response = await client.PostAsync($"api/walls/{wallId}/images", body);

        // The action only gets to its content-type check by reading the section off the body.
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
    }

    private static async Task<WebApplication> StartAsync(Action<IServiceCollection> services)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Production",
            ContentRootPath = AppContext.BaseDirectory,
        });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton(new WallCapturePipelineOptions());
        services(builder.Services);
        builder.Services.AddAuthentication(ApiKeyAuthenticationHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, ApiKeyPrincipalAuthHandler>(ApiKeyAuthenticationHandler.SchemeName, _ => { });
        builder.Services.AddAuthorization(o =>
        {
            o.AddPolicy(BlocwerkPolicies.AnyApiKey, p => p.RequireAuthenticatedUser());
            o.AddPolicy(BlocwerkPolicies.WallApiKey, p => p.RequireAuthenticatedUser());
        });
        builder.Services.AddAntiforgery();
        builder.Services.AddControllers().AddApplicationPart(typeof(WallCapturesController).Assembly);

        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseAntiforgery();
        app.MapControllers();
        await app.StartAsync();
        return app;
    }

    private static HttpClient Client(WebApplication app) => new()
    {
        BaseAddress = new Uri(app.Services.GetRequiredService<IServer>().Features
            .Get<IServerAddressesFeature>()!.Addresses.First() + "/"),
    };

    private static MultipartFormDataContent Multipart(params (string Name, string Type, int Length)[] files)
    {
        var content = new MultipartFormDataContent { { new StringContent("ignored"), "note" } };
        foreach (var (name, type, length) in files)
        {
            var part = new ByteArrayContent(new byte[length]);
            part.Headers.ContentType = new MediaTypeHeaderValue(type);
            content.Add(part, "file", name);
        }

        return content;
    }
}
