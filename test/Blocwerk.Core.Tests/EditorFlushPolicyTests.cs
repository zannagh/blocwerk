// <copyright file="EditorFlushPolicyTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Blocwerk.Web.State;
using Xunit;

namespace Blocwerk.Core.Tests;

/// <summary>
/// What the pre-deploy flush does per editor, and the kiosk case end to end: a tablet acting as a member drafts as the
/// member (by the cookie the service reads, never a parameter), an anonymous tablet keeps its create on the device.
/// </summary>
public class EditorFlushPolicyTests
{
    [Theory]
    [InlineData(false, false, false, EditFlushAction.Nothing)]
    [InlineData(true, true, false, EditFlushAction.Nothing)]
    [InlineData(true, false, false, EditFlushAction.SaveOnServer)]
    [InlineData(true, false, true, EditFlushAction.KeepOnDevice)]
    public void BoulderCreate(bool dirty, bool submitting, bool anonymous, EditFlushAction expected)
    {
        Assert.Equal(expected, EditorFlushPolicy.BoulderCreate(dirty, submitting, anonymous));
    }

    [Theory]
    [InlineData(false, false, true, EditFlushAction.Nothing)]
    [InlineData(true, false, true, EditFlushAction.SaveOnServer)]
    [InlineData(true, false, false, EditFlushAction.KeepOnDevice)]
    [InlineData(true, true, false, EditFlushAction.Nothing)]
    public void BoulderRevise(bool dirty, bool submitting, bool isDraft, EditFlushAction expected)
    {
        Assert.Equal(expected, EditorFlushPolicy.BoulderRevise(dirty, submitting, isDraft));
    }

    [Fact]
    public void InlineEditsAndNewWallsAreKeptNeverSent()
    {
        Assert.Equal(EditFlushAction.KeepOnDevice, EditorFlushPolicy.InlineBoulderEdit(dirty: true));
        Assert.Equal(EditFlushAction.Nothing, EditorFlushPolicy.InlineBoulderEdit(dirty: false));
        Assert.Equal(EditFlushAction.KeepOnDevice, EditorFlushPolicy.WallCreate(dirty: true, submitting: false));
        Assert.Equal(EditFlushAction.Nothing, EditorFlushPolicy.WallCreate(dirty: true, submitting: true));
    }

    [Theory]
    [InlineData(false, false, true, true, false, EditFlushAction.SaveOnServer)]
    [InlineData(false, false, true, true, true, EditFlushAction.Nothing)]
    [InlineData(false, false, true, false, false, EditFlushAction.Nothing)]
    [InlineData(false, false, false, true, false, EditFlushAction.Nothing)]
    [InlineData(true, true, true, true, false, EditFlushAction.CannotKeep)]
    [InlineData(true, false, true, false, false, EditFlushAction.Nothing)]
    public void WallEditor(bool manualAlignment, bool alignmentWork, bool canEdit, bool pending, bool submitting, EditFlushAction expected)
    {
        Assert.Equal(expected, EditorFlushPolicy.WallEditor(manualAlignment, alignmentWork, canEdit, pending, submitting));
    }

    /// <summary>W4 is a product decision still open: until it is made, the flush saves the batch exactly like the button.</summary>
    [Fact]
    public void TheWallFlushSavesTheWholeBatchUntilDecidedOtherwise()
    {
        Assert.Equal(WallFlushScope.Everything, EditorFlushPolicy.WallFlush);
    }

    [Theory]
    [InlineData(true, false, RestoredCreateId.Reuse)]
    [InlineData(false, false, RestoredCreateId.Fresh)]
    [InlineData(true, true, RestoredCreateId.AlreadySaved)]
    [InlineData(false, true, RestoredCreateId.AlreadySaved)]
    public void ARestoredCreateOnlyReusesItsIdWhenNothingCanCollide(bool fromDeploy, bool exists, RestoredCreateId expected)
    {
        Assert.Equal(expected, EditorFlushPolicy.RestoreCreate(fromDeploy, exists));
    }

    /// <summary>Kept copies are partitioned by who is at the browser, in the shape deploy-drafts.js builds.</summary>
    [Fact]
    public void KeptCopyKeysCarryTheIdentity()
    {
        var user = Guid.NewGuid();
        var key = Guid.NewGuid();
        var notKiosk = AnonymousSettingFixture.KioskContextFor(isKiosk: false, null, null);
        var tablet = AnonymousSettingFixture.KioskContextFor(isKiosk: true, Guid.NewGuid(), key);

        Assert.Equal($"{user}|boulder-create:w", KeptCopyKey.For(user, notKiosk, "boulder-create:w"));
        Assert.Equal($"k{key}-{user}|boulder-create:w", KeptCopyKey.For(user, tablet, "boulder-create:w"));
        Assert.Equal($"k{key}-anon|boulder-create:w", KeptCopyKey.For(null, tablet, "boulder-create:w"));
        Assert.Equal("anon|wall-create", KeptCopyKey.For(Guid.Empty, null, "wall-create"));
    }

    /// <summary>
    /// The flush's draft save for a kiosk acting as a member is the ordinary signed-in create: credited to the member
    /// the cookie names, a draft, and idempotent on the client id, so a second flush cannot make a second draft.
    /// </summary>
    [Fact]
    public async Task AKioskActingAsAMemberDraftsAsTheMember()
    {
        using var fixture = await AnonymousSettingFixture.CreateAsync();
        var member = await fixture.AddConsentingMemberAsync("alice");
        fixture.Harness.ActingUser = member;
        var kiosk = fixture.LiveKioskContext();
        var boulders = new BoulderService(
            new KioskStampingFactory(fixture.Harness.DbContextFactory, kiosk),
            fixture.Harness.CurrentUser,
            fixture.Harness.ActivityLog,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<BoulderService>.Instance,
            kiosk);
        var clientId = Guid.NewGuid();
        List<BoulderHoldInput> holds = [new(fixture.FirstHoldId, HoldType.Normal, HoldUsage.HandAndFoot)];

        var first = await boulders.CreateBoulderAsync(fixture.Harness.WallId, "Mellow Crimp", null, holds, isDraft: true, id: clientId);
        var again = await boulders.CreateBoulderAsync(fixture.Harness.WallId, "Mellow Crimp", null, holds, isDraft: true, id: clientId);

        var stored = Assert.Single(await fixture.StoredBouldersAsync());
        Assert.Equal(member.Id, stored.CreatedByUserId);
        Assert.True(stored.IsDraft);
        Assert.Equal(first.Id, again.Id);

        // The same tablet with nobody picked never gets a draft: the policy keeps it on the device.
        Assert.Equal(EditFlushAction.KeepOnDevice, EditorFlushPolicy.BoulderCreate(dirty: true, submitting: false, anonymous: true));
    }
}
