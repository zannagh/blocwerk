// <copyright file="AutomationApiFixture.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Security.Claims;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Services;
using Blocwerk.Web.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Blocwerk.Core.Tests.Automation;

/// <summary>
/// The automation API's controllers over a <see cref="WallTestHarness"/>: a real change journal (registry over the
/// harness's database), the audit acting as the harness's user, and contexts that journal like production's.
/// </summary>
internal static class AutomationApiFixture
{
    /// <summary>Every key the controllers are tested with.</summary>
    public static TheoryData<string> KeyNames() => new(
        "personal write",
        "personal read-only",
        "wall write",
        "wall read-only",
        "other wall write",
        "kiosk",
        "installation");

    /// <summary>The keys the wall-admin guard admits: write keys, a wall key only on its own wall.</summary>
    public static bool IsWallAdminKey(string name) => name is "personal write" or "wall write";

    /// <summary>The principal named <paramref name="name"/> (see <see cref="KeyNames"/>) for <paramref name="wallId"/>.</summary>
    public static ClaimsPrincipal Key(string name, Guid wallId) => name switch
    {
        "personal write" => ApiKeys.Personal(),
        "personal read-only" => ApiKeys.Personal(allowWrite: false),
        "wall write" => ApiKeys.Wall(wallId),
        "wall read-only" => ApiKeys.Wall(wallId, allowWrite: false),
        "other wall write" => ApiKeys.Wall(Guid.NewGuid()),
        "kiosk" => ApiKeys.Kiosk(wallId),
        "installation" => ApiKeys.Installation(),
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, null),
    };

    /// <summary>A change journal whose registry writes go to the harness's database.</summary>
    public static ChangeJournal Journal(WallTestHarness h) => new(() => h.RootContextFactory.CreateDbContext());

    public static ApiWriteAudit Audit(WallTestHarness h, ChangeJournal journal) =>
        new(journal, h.CurrentUser, NullLogger<ApiWriteAudit>.Instance);

    /// <summary>Gives <paramref name="controller"/> a request authenticated as <paramref name="key"/>.</summary>
    public static T As<T>(this T controller, ClaimsPrincipal key)
        where T : ControllerBase
    {
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = key } };
        return controller;
    }

    /// <summary>The journal batches the automation API wrote, oldest first.</summary>
    public static async Task<List<ChangeJournalBatch>> ApiBatchesAsync(WallTestHarness h)
    {
        await using var db = h.CreateContext();
        var batches = await db.ChangeJournalBatches.AsNoTracking()
            .Where(b => b.Label.StartsWith(ApiWriteAudit.LabelPrefix))
            .ToListAsync();
        return batches.OrderBy(b => b.CreatedAt).ToList();
    }

    public static int Status(IActionResult result) => result switch
    {
        ObjectResult o => o.StatusCode ?? StatusCodes.Status200OK,
        StatusCodeResult s => s.StatusCode,
        _ => throw new InvalidOperationException(result.GetType().Name),
    };
}

/// <summary>Contexts over the harness's database that run the change journal's interceptor, as production's do.</summary>
internal sealed class JournalledDbContextFactory(WallTestHarness h, ChangeJournal journal) : IDbContextFactory<BlocwerkDbContext>
{
    public BlocwerkDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<BlocwerkDbContext>()
            .UseSqlite(h.DbContextFactory.ConnectionString)
            .AddInterceptors(new ChangeJournalInterceptor(journal))
            .Options;
        return new SqliteBlocwerkDbContext(options);
    }
}
