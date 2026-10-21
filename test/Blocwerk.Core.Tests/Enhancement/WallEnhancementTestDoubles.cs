// <copyright file="WallEnhancementTestDoubles.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Data;
using Blocwerk.Core.Enhancement;
using Blocwerk.Core.Entities;
using Blocwerk.Core.HoldStacks;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using NSubstitute;

namespace Blocwerk.Core.Tests.Enhancement;

/// <summary>Contexts that journal every save, like the app's.</summary>
internal sealed class EnhancementJournallingFactory(string connection, ChangeJournal journal) : IDbContextFactory<BlocwerkDbContext>
{
    public BlocwerkDbContext CreateDbContext() => TestDb.Create(connection, new ChangeJournalInterceptor(journal));
}

/// <summary>The fixture's actor factory: substitutes, or the real services when the fixture says so.</summary>
internal sealed class EnhancementTestActors(WallEnhancementFixture fixture) : IWallEnhancementActorFactory
{
    public Task<WallEnhancementActors> CreateAsync(Guid userId, CancellationToken ct) => Task.FromResult(
        fixture.UseRealActors
            ? fixture.RealActors()
            : new WallEnhancementActors(
                Substitute.For<IHoldOutlineUpgradeService>(), Substitute.For<IHoldStackService>(), Substitute.For<IHoldShapeCleanupService>(),
                Substitute.For<IHoldColorProposalService>(), fixture.Duplicates, fixture.Links, fixture.Proposals));
}

/// <summary>A step whose work the test supplies; it counts its runs.</summary>
internal sealed class FakeEnhancementStep(
    string key, int order, WallEnhancementItemKind kind, Func<WallEnhancementStepContext, CancellationToken, Task<WallEnhancementStepOutcome>> run)
    : IWallEnhancementStep
{
    public string Key => key;

    public int Order => order;

    public string Title => $"Fake {key}";

    public WallEnhancementItemKind Kind => kind;

    public int Runs { get; private set; }

    public async Task<WallEnhancementStepOutcome> RunAsync(WallEnhancementStepContext context, CancellationToken ct)
    {
        Runs++;
        return await run(context, ct);
    }

    /// <summary>A step that changes the holds the way a service would: on a journalling context, as the acting admin.</summary>
    public static FakeEnhancementStep Writing(
        WallEnhancementFixture fixture, string key, int order, WallEnhancementItemKind kind, IReadOnlyList<Guid> holds, Action<Hold> change) =>
        new(key, order, kind, async (ctx, ct) =>
        {
            await using var db = await fixture.Journalling.CreateDbContextAsync(ct);
            db.CurrentUserId = ctx.UserId;
            foreach (var hold in await db.Holds.Where(h => holds.Contains(h.Id)).ToListAsync(ct))
            {
                change(hold);
            }

            await db.SaveChangesAsync(ct);
            return WallEnhancementStepOutcome.Done($"{holds.Count} changed");
        });

    /// <summary>A step that only suggests.</summary>
    public static FakeEnhancementStep Suggesting(string key, int order, WallEnhancementItemKind kind, params WallEnhancementSuggestion[] suggestions) =>
        new(key, order, kind, (_, _) => Task.FromResult(new WallEnhancementStepOutcome($"{suggestions.Length} suggested", suggestions)));
}

/// <summary>A context factory whose first context throws on save, as a dropped connection would.</summary>
internal sealed class FailOnceFactory(IDbContextFactory<BlocwerkDbContext> inner) : IDbContextFactory<BlocwerkDbContext>
{
    public bool Failed { get; private set; }

    public BlocwerkDbContext CreateDbContext()
    {
        if (!Failed)
        {
            Failed = true;
            throw new InvalidOperationException("connection dropped");
        }

        return inner.CreateDbContext();
    }
}
