// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Net;
using Blocwerk.Authentication.Authorization;
using Blocwerk.Authentication.Handlers;
using Blocwerk.Core.Services;
using Blocwerk.Web.Controllers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using NSubstitute;

namespace Blocwerk.Core.Tests;

/// <summary>
/// <c>/api/v1/admin/journal/*</c> over HTTP: anonymous is 401, a key the policy does not admit is 403 without reaching the
/// service, a personal write key whose owner is not an app admin is 403 by the service's refusal, and replay and revert
/// are dry runs unless the caller says otherwise.
/// </summary>
public sealed class ChangeJournalAdminEndpointTests
{
    private static readonly Guid Wall = Guid.NewGuid();

    public static TheoryData<string, string> Routes => new()
    {
        { "GET", $"/api/v1/admin/journal/walls/{Wall}/batches" },
        { "GET", $"/api/v1/admin/journal/export?batchId={Guid.NewGuid()}" },
        { "POST", "/api/v1/admin/journal/replay" },
        { "POST", $"/api/v1/admin/journal/batches/{Guid.NewGuid()}/revert" },
    };

    [Fact]
    public void TheController_IsAPersonalWriteKeyRoute_UnderTheApiKeySurface()
    {
        var authorize = Assert.Single(typeof(ChangeJournalAdminController).GetCustomAttributes(typeof(AuthorizeAttribute), true).Cast<AuthorizeAttribute>());
        Assert.Equal(BlocwerkPolicies.HumanOrUserApiKey, authorize.Policy);
        Assert.Equal(ApiKeyAuthenticationHandler.SchemeName, authorize.AuthenticationSchemes);
        Assert.True(ApiKeySurface.Covers(new PathString("/api/v1/admin/journal/replay")));
    }

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task Anonymous_Is401(string method, string path)
    {
        var journal = Substitute.For<IChangeJournalAdminService>();
        await using var host = await JournalApiHost.StartAsync(journal);

        Assert.Equal(HttpStatusCode.Unauthorized, (await host.SendAsync(method, path, who: null)).StatusCode);
        Assert.Empty(journal.ReceivedCalls());
    }

    [Theory]
    [InlineData("read-only-personal-key")]
    [InlineData("wall-key")]
    [InlineData("kiosk-key")]
    public async Task AKeyThePolicyDoesNotAdmit_Is403_WithoutReachingTheService(string who)
    {
        var journal = Substitute.For<IChangeJournalAdminService>();
        await using var host = await JournalApiHost.StartAsync(journal);

        foreach (var (method, path) in RoutePairs())
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await host.SendAsync(method, path, who)).StatusCode);
        }

        Assert.Empty(journal.ReceivedCalls());
    }

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task APersonalWriteKeyOfANonAdmin_Is403(string method, string path)
    {
        var journal = Substitute.For<IChangeJournalAdminService>();
        var refusal = new UnauthorizedAccessException("not an admin");
        journal.EnsureAdminAsync(default).ReturnsForAnyArgs(Task.FromException<Guid>(refusal));
        journal.ListBatchesAsync(default, default, default, default).ReturnsForAnyArgs(Task.FromException<IReadOnlyList<ChangeJournalBatchSummary>>(refusal));
        journal.ExportAsync(default!, default).ReturnsForAnyArgs(Task.FromException<ChangeJournalPackage>(refusal));
        journal.ReplayAsync(default!, default, default, default).ReturnsForAnyArgs(Task.FromException<ChangeJournalReplayReport>(refusal));
        journal.RevertAsync(default, default, default, default).ReturnsForAnyArgs(Task.FromException<ChangeJournalRevertResult>(refusal));
        await using var host = await JournalApiHost.StartAsync(journal);

        Assert.Equal(HttpStatusCode.Forbidden, (await host.SendAsync(method, path, "personal-write-key")).StatusCode);
    }

    [Fact]
    public async Task ReplayAndRevert_AreDryRunsByDefault_AndAGzipBodyIsRead()
    {
        var journal = Substitute.For<IChangeJournalAdminService>();
        journal.ReplayAsync(default!, default, default, default)
            .ReturnsForAnyArgs(c => new ChangeJournalReplayReport(false, c.ArgAt<bool>(1), [], [], null));
        journal.RevertAsync(default, default, default, default)
            .ReturnsForAnyArgs(c => new ChangeJournalRevertResult(false, c.ArgAt<Guid>(0), null, [], null, c.ArgAt<bool>(1)));
        await using var host = await JournalApiHost.StartAsync(journal);
        var batchId = Guid.NewGuid();

        var replay = await host.SendAsync("POST", "/api/v1/admin/journal/replay", "personal-write-key", await GzipPackageAsync("m-1"));
        var revert = await host.SendAsync("POST", $"/api/v1/admin/journal/batches/{batchId}/revert", "personal-write-key");

        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(HttpStatusCode.OK, revert.StatusCode);
        await journal.Received(1).ReplayAsync(Arg.Is<ChangeJournalPackage>(p => p.MigrationId == "m-1"), true, false, Arg.Any<CancellationToken>());
        await journal.Received(1).RevertAsync(batchId, true, false, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ARealReplayThatWasRefused_Is409_AndAnUnreadableBodyIs400()
    {
        var journal = Substitute.For<IChangeJournalAdminService>();
        journal.ReplayAsync(default!, default, default, default)
            .ReturnsForAnyArgs(new ChangeJournalReplayReport(false, false, [], ["Schema parity mismatch"], null));
        await using var host = await JournalApiHost.StartAsync(journal);

        var refused = await host.SendAsync("POST", "/api/v1/admin/journal/replay?dryRun=false", "personal-write-key", await GzipPackageAsync("m-1"));
        var garbage = await host.SendAsync("POST", "/api/v1/admin/journal/replay", "personal-write-key", new StringContent("not json"));

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, garbage.StatusCode);
    }

    private static IEnumerable<(string Method, string Path)> RoutePairs() =>
        Routes.Select(row => ((string)row[0], (string)row[1]));

    private static async Task<HttpContent> GzipPackageAsync(string migrationId)
    {
        using var buffer = new MemoryStream();
        await ChangeJournalPackageJson.WriteGzipAsync(new ChangeJournalPackage { MigrationId = migrationId }, buffer, default);
        return new ByteArrayContent(buffer.ToArray());
    }
}
