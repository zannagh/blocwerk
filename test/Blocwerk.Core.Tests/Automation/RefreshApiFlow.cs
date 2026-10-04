// <copyright file="RefreshApiFlow.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Security.Claims;
using Blocwerk.Core.Refresh;
using Blocwerk.Core.Services;
using Blocwerk.Core.Tests.Refresh;
using Blocwerk.Web.Endpoints;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Tests.Automation;

/// <summary>The steps of a scripted panel update that are not controller actions: the file drop and the wall's generation.</summary>
internal static class RefreshApiFlow
{
    /// <summary>Drops <paramref name="photos"/> photos through the upload route as <paramref name="key"/>, audited like production.</summary>
    public static async Task UploadAsync(
        WallTestHarness h, RefreshScenario s, ChangeJournal journal, ClaimsPrincipal key, Guid refreshId, int photos)
    {
        for (var i = 0; i < photos; i++)
        {
            var bytes = ExifJpeg.Build(CaptureScenario.TinyJpeg(seed: 40 + i));
            var http = new DefaultHttpContext { User = key };
            http.Request.Body = new MemoryStream(bytes);
            http.Request.ContentLength = bytes.Length;

            var result = await WallRefreshUploadEndpoint.HandleAsync(
                refreshId, $"IMG_{i}.jpg", http, s.Service, s.Capture.Options, CancellationToken.None, AutomationApiFixture.Audit(h, journal));

            Assert.Null(Assert.IsType<Ok<RefreshFile>>(result).Value!.Problem);
        }
    }

    /// <summary>The wall's live hold generation: 0 before a panel update is promoted, 1 after.</summary>
    public static async Task<int> GenerationAsync(WallTestHarness h)
    {
        await using var db = h.CreateContext();
        return await db.Walls.Where(w => w.Id == h.WallId).Select(w => w.CurrentGeneration).SingleAsync();
    }
}
