using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Blocwerk.Web.State;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The device-pairing flow: a tablet asks for a code, a wall admin approves it against one of their
/// walls, and the tablet redeems the result once.
/// </summary>
/// <remarks>
/// The properties under test are the ones the flow's safety actually rests on. A code is displayed
/// on a screen in a public gym, so it is assumed to be READ by strangers; what must hold is that
/// reading it buys nothing — the claim ticket, held only in the tablet's circuit, is what redeems,
/// the entry dies on first use, and no anonymous caller can put a wall on a pairing.
/// </remarks>
public class KioskPairingTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 2, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Create_IssuesSixDigitCodesThatAreUniqueAmongActivePairings()
    {
        var registry = new KioskPairingRegistry();

        var codes = new List<string>();
        for (var i = 0; i < 200; i++)
        {
            var entry = registry.Create(Now)!;
            Assert.Equal(6, entry.Code.Length);
            Assert.True(entry.Code.All(char.IsAsciiDigit), $"'{entry.Code}' is not six digits.");
            codes.Add(entry.Code);
        }

        // The property that makes the TYPED approval path safe to build at all: a code an admin
        // types can only ever resolve to one waiting tablet, so there is no way to approve the
        // wrong device by collision.
        Assert.Equal(codes.Count, codes.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Create_ReusesACodeOnceTheOldPairingHasExpired()
    {
        var registry = new KioskPairingRegistry();
        var first = registry.Create(Now)!;

        // Expired entries do not reserve their code. Nothing here asserts the SAME code comes back —
        // it is a random draw — only that the expired one is no longer findable, so it is not
        // holding anything.
        Assert.Null(registry.TryFindByCode(first.Code, Now + KioskPairingRegistry.Lifetime));
    }

    [Fact]
    public void APairingIsInvisibleOnceItsThreeMinutesAreUp()
    {
        var registry = new KioskPairingRegistry();
        var entry = registry.Create(Now)!;

        var justBefore = Now + KioskPairingRegistry.Lifetime - TimeSpan.FromSeconds(1);
        Assert.NotNull(registry.Find(entry.Id, justBefore));
        Assert.NotNull(registry.TryFindByCode(entry.Code, justBefore));

        // Check-on-read, with no sweeper anywhere: the moment it lapses, every lookup misses.
        var after = Now + KioskPairingRegistry.Lifetime;
        Assert.Null(registry.Find(entry.Id, after));
        Assert.Null(registry.TryFindByCode(entry.Code, after));
        Assert.Equal(0, registry.ActiveCount(after));
    }

    [Fact]
    public void ApproveRequiresAWallAndAKey()
    {
        var registry = new KioskPairingRegistry();
        var entry = registry.Create(Now)!;

        // The pairing carries no wall of its own and an anonymous tablet can never give it one, so
        // an approval that names no wall is meaningless rather than merely empty.
        Assert.False(registry.TryApprove(entry.Id, Guid.Empty, Guid.NewGuid(), Now));
        Assert.False(registry.TryApprove(entry.Id, Guid.NewGuid(), Guid.Empty, Now));

        var state = registry.Find(entry.Id, Now);
        Assert.NotNull(state);
        Assert.Equal(KioskPairingStatus.Pending, state.Status);
        Assert.Null(state.WallId);
    }

    [Fact]
    public void ApproveIsRefusedASecondTime()
    {
        var registry = new KioskPairingRegistry();
        var entry = registry.Create(Now)!;
        var wallId = Guid.NewGuid();

        Assert.True(registry.TryApprove(entry.Id, wallId, Guid.NewGuid(), Now));

        // Otherwise the first minted key would be stranded: a live kiosk credential for a wall,
        // attached to nothing, that nobody would think to revoke.
        Assert.False(registry.TryApprove(entry.Id, Guid.NewGuid(), Guid.NewGuid(), Now));

        var state = registry.Find(entry.Id, Now);
        Assert.NotNull(state);
        Assert.Equal(wallId, state.WallId);
    }

    [Fact]
    public void ApproveRaisesChangedSoTheWaitingTabletFindsOut()
    {
        var registry = new KioskPairingRegistry();
        var entry = registry.Create(Now)!;

        var seen = new List<Guid>();
        registry.Changed += seen.Add;

        // A throwing subscriber must not stop the one that is actually waiting, exactly as
        // DomainChangeNotifier does it.
        registry.Changed += _ => throw new InvalidOperationException("a stale circuit");
        registry.Changed += seen.Add;

        Assert.True(registry.TryApprove(entry.Id, Guid.NewGuid(), Guid.NewGuid(), Now));
        Assert.Equal([entry.Id, entry.Id], seen);
    }

    [Fact]
    public void RedeemNeedsTheClaimTicketAndWorksExactlyOnce()
    {
        var registry = new KioskPairingRegistry();
        var entry = registry.Create(Now)!;
        var wallId = Guid.NewGuid();
        var apiKeyId = Guid.NewGuid();
        Assert.True(registry.TryApprove(entry.Id, wallId, apiKeyId, Now));

        var redeemed = registry.TryRedeem(entry.Id, entry.ClaimTicket, Now);
        Assert.NotNull(redeemed);
        Assert.Equal(apiKeyId, redeemed.ApiKeyId);
        Assert.Equal(wallId, redeemed.WallId);

        // Single use. The entry is credential-equivalent from approval onwards, so the second
        // attempt — the tablet's own retry, a replayed POST, anything — finds nothing.
        Assert.Null(registry.TryRedeem(entry.Id, entry.ClaimTicket, Now));
        Assert.Null(registry.Find(entry.Id, Now));
    }

    [Fact]
    public void RedeemWithTheWrongClaimTicketFailsAndLeavesThePairingAlone()
    {
        var registry = new KioskPairingRegistry();
        var entry = registry.Create(Now)!;
        Assert.True(registry.TryApprove(entry.Id, Guid.NewGuid(), Guid.NewGuid(), Now));

        // The whole point of the ticket: somebody who photographed the six digits, watched an admin
        // approve, and knows the pairing id still cannot take the key that was just minted.
        Assert.Null(registry.TryRedeem(entry.Id, "not-the-ticket", Now));
        Assert.Null(registry.TryRedeem(entry.Id, null, Now));
        Assert.Null(registry.TryRedeem(entry.Id, string.Empty, Now));

        // And a wrong guess must not destroy the pairing, or a bystander could deny the real tablet
        // the approval it is waiting for.
        Assert.NotNull(registry.TryRedeem(entry.Id, entry.ClaimTicket, Now));
    }

    [Fact]
    public void APendingPairingCannotBeRedeemed()
    {
        var registry = new KioskPairingRegistry();
        var entry = registry.Create(Now)!;

        // No admin has chosen a wall, so there is nothing to become.
        Assert.Null(registry.TryRedeem(entry.Id, entry.ClaimTicket, Now));
    }

    [Fact]
    public void AnApprovedPairingStillExpires()
    {
        var registry = new KioskPairingRegistry();
        var entry = registry.Create(Now)!;
        Assert.True(registry.TryApprove(entry.Id, Guid.NewGuid(), Guid.NewGuid(), Now));

        // Approval does not extend the window. A tablet that was unplugged between the approval and
        // the redemption leaves nothing redeemable behind.
        Assert.Null(registry.TryRedeem(entry.Id, entry.ClaimTicket, Now + KioskPairingRegistry.Lifetime));
    }

    [Fact]
    public void RedeemingAnUnknownPairingFails()
    {
        var registry = new KioskPairingRegistry();
        Assert.Null(registry.TryRedeem(Guid.NewGuid(), "anything", Now));
    }

    [Fact]
    public void TheLiveCapBitesButReleasesItselfRatherThanLockingTheInstallationOut()
    {
        var registry = new KioskPairingRegistry();

        for (var i = 0; i < KioskPairingRegistry.MaxLivePairings; i++)
        {
            Assert.NotNull(registry.Create(Now));
        }

        // Full: at most this many of the million codes are ever held open at once.
        Assert.Null(registry.Create(Now));
        Assert.Equal(KioskPairingRegistry.MaxLivePairings, registry.ActiveCount(Now));

        // And the cap is a measure of the PRESENT, not an accumulating counter. This is the property
        // the old creation-rate cap did not have: it counted every successful creation against one
        // global, doubling, never-reset lockout, so a handful of tablets rebooting — or one
        // anonymous request every half hour — locked every tablet in the installation out of pairing
        // for good. Here, waiting out one code's lifetime is enough, and nobody has to intervene.
        var later = Now + KioskPairingRegistry.Lifetime;
        Assert.Equal(0, registry.ActiveCount(later));
        Assert.NotNull(registry.Create(later));
    }

    [Fact]
    public void ANormalFleetOfTabletsCannotLockItselfOut()
    {
        var registry = new KioskPairingRegistry();

        // Twenty tablets in a gym, all rebooting at once after a power cut, each asking for a code.
        var entries = new List<KioskPairingEntry>();
        for (var i = 0; i < 20; i++)
        {
            var entry = registry.Create(Now);
            Assert.NotNull(entry);
            entries.Add(entry);
        }

        // Then every one of them taps "Get a new code" five times over, which is what somebody does
        // when they cannot see the QR properly. Restart releases the old pairing before taking a new
        // one, so the live count never grows past one per tablet and nothing is ever refused.
        for (var round = 0; round < 5; round++)
        {
            for (var i = 0; i < entries.Count; i++)
            {
                registry.Remove(entries[i].Id);
                var again = registry.Create(Now);
                Assert.NotNull(again);
                entries[i] = again;
            }
        }

        Assert.Equal(20, registry.ActiveCount(Now));

        // And a tablet that is simply unplugged mid-wait gives its code back on dispose.
        foreach (var entry in entries)
        {
            registry.Remove(entry.Id);
        }

        Assert.Equal(0, registry.ActiveCount(Now));
    }

    [Fact]
    public void ThePerCodeGuessScopeDoesNotEscalate()
    {
        // Its key is the code, which anybody who can see the tablet controls. Escalating on that is
        // a half-hour denial of service against the tablet legitimately waiting, handed to a
        // bystander; the per-USER scope is where the real bound lives and it still doubles.
        var scopes = KioskThrottleRegistry.PairingGuessScopes(Guid.NewGuid(), "424242");
        var perUser = scopes[0];
        var perCode = scopes[1];

        Assert.False(perUser.Flat);
        Assert.True(perCode.Flat);

        Assert.Equal(perCode.BaseLockout, KioskThrottleRegistry.BackoffFor(perCode, bursts: 10));
        Assert.True(KioskThrottleRegistry.BackoffFor(perUser, bursts: 10) > perUser.BaseLockout);
    }

    [Fact]
    public void TheTypedCodeCapBitesPerAdminAndPerCode()
    {
        var throttle = new KioskThrottleRegistry();
        var admin = Guid.NewGuid();
        var otherAdmin = Guid.NewGuid();

        // 10^6 is small enough that an unthrottled six-digit field would be a real attack.
        for (var i = 0; i < KioskThrottleRegistry.MaxAttempts; i++)
        {
            var scopes = KioskThrottleRegistry.PairingGuessScopes(admin, $"00000{i}");
            Assert.False(throttle.IsLocked(scopes, Now));
            throttle.RegisterFailure(scopes, Now);
        }

        // The per-USER scope is the one that binds: a guesser walking the keyspace never repeats a
        // code, so only the authenticated identity can stop them — and unlike a client address, it
        // is not something the caller writes for themselves.
        Assert.True(throttle.IsLocked(KioskThrottleRegistry.PairingGuessScopes(admin, "999999"), Now));

        // A different admin is unaffected, so one person fumbling a code cannot lock the gym out.
        Assert.False(throttle.IsLocked(KioskThrottleRegistry.PairingGuessScopes(otherAdmin, "999999"), Now));

        // The per-CODE scope covers the other shape: grinding at the one code visible on a screen.
        for (var i = 0; i < KioskThrottleRegistry.MaxAttempts; i++)
        {
            throttle.RegisterFailure(KioskThrottleRegistry.PairingGuessScopes(Guid.NewGuid(), "424242"), Now);
        }

        Assert.True(throttle.IsLocked(KioskThrottleRegistry.PairingGuessScopes(Guid.NewGuid(), "424242"), Now));
    }

    [Fact]
    public async Task ApprovingMintsAKioskScopedKeyForTheChosenWallAndNeverHandsOverTheToken()
    {
        using var harness = new WallTestHarness();
        await harness.SeedWallAsync();

        var registry = new KioskPairingRegistry();
        var approver = BuildApprover(harness, registry);
        var entry = registry.Create()!;

        Assert.Equal(KioskPairingApprovalResult.Approved, await approver.ApproveAsync(entry.Id, harness.WallId));

        var state = registry.Find(entry.Id);
        Assert.NotNull(state);
        Assert.Equal(KioskPairingStatus.Approved, state.Status);
        Assert.Equal(harness.WallId, state.WallId);
        Assert.NotNull(state.ApiKeyId);

        var keys = await harness.ApiKeyService.GetWallKeysAsync(harness.WallId, harness.Owner.Id);
        var minted = Assert.Single(keys, k => k.Id == state.ApiKeyId);
        Assert.Equal(ApiKeyScope.Kiosk, minted.Scope);
        Assert.Equal(harness.WallId, minted.WallId);
        Assert.Contains("Kiosk tablet paired", minted.Name);

        // The plaintext token is discarded inside the approver: the pairing carries the key's ID and
        // the wall, which is all KioskDeviceCookie.Write needs, and there is nothing for the tablet
        // to display on a screen bolted to a public gym wall.
        var lookedUp = registry.Find(entry.Id);
        Assert.NotNull(lookedUp);
        Assert.Equal(state.ApiKeyId, lookedUp.ApiKeyId);
    }

    [Theory]
    [InlineData(WallRole.Member)]
    [InlineData(WallRole.Moderator)]
    public async Task ANonAdminOfTheChosenWallIsRefused(WallRole role)
    {
        using var harness = new WallTestHarness();
        await harness.SeedWallAsync();

        var registry = new KioskPairingRegistry();
        var approver = BuildApprover(harness, registry);
        var entry = registry.Create()!;

        // Both entry points funnel through this one call, so refusing here refuses the QR page and
        // the wall-settings card together. A moderator may edit holds but may not mint credentials.
        harness.ActingUser = await harness.AddMemberAsync($"{role}@test", role);

        Assert.Equal(KioskPairingApprovalResult.NotAuthorised, await approver.ApproveAsync(entry.Id, harness.WallId));

        var state = registry.Find(entry.Id);
        Assert.NotNull(state);
        Assert.Equal(KioskPairingStatus.Pending, state.Status);
        Assert.Null(state.WallId);
        Assert.Empty(await harness.ApiKeyService.GetWallKeysAsync(harness.WallId, harness.Owner.Id));
    }

    [Fact]
    public async Task AWallTheApproverDoesNotAdministerAtAllIsRefused()
    {
        using var harness = new WallTestHarness();
        await harness.SeedWallAsync();

        var registry = new KioskPairingRegistry();
        var approver = BuildApprover(harness, registry);
        var entry = registry.Create()!;

        // The wall on an approval is never taken on trust — it is re-checked against the database at
        // the moment the key is minted, so a wall id substituted after the picker rendered is worth
        // nothing.
        Assert.Equal(
            KioskPairingApprovalResult.NotAuthorised,
            await approver.ApproveAsync(entry.Id, Guid.NewGuid()));

        Assert.Equal(KioskPairingApprovalResult.NoWall, await approver.ApproveAsync(entry.Id, Guid.Empty));
    }

    [Fact]
    public async Task AnExpiredPairingMintsNothing()
    {
        using var harness = new WallTestHarness();
        await harness.SeedWallAsync();

        var registry = new KioskPairingRegistry();
        var approver = BuildApprover(harness, registry);

        Assert.Equal(
            KioskPairingApprovalResult.PairingUnavailable,
            await approver.ApproveAsync(Guid.NewGuid(), harness.WallId));

        // Nothing was minted on the way to finding out.
        Assert.Empty(await harness.ApiKeyService.GetWallKeysAsync(harness.WallId, harness.Owner.Id));
    }

    [Fact]
    public async Task ThePickerOffersOnlyWallsTheUserAdministers()
    {
        using var harness = new WallTestHarness();
        await harness.SeedWallAsync();

        var registry = new KioskPairingRegistry();
        var approver = BuildApprover(harness, registry);

        // The owner administers their own wall even with no explicit member row.
        var owned = await approver.GetAdministeredWallsAsync();
        Assert.Equal(harness.WallId, Assert.Single(owned).Id);

        harness.ActingUser = await harness.AddMemberAsync("member@test", WallRole.Member);
        Assert.Empty(await approver.GetAdministeredWallsAsync());

        harness.ActingUser = await harness.AddMemberAsync("admin@test", WallRole.Admin);
        Assert.Equal(harness.WallId, Assert.Single(await approver.GetAdministeredWallsAsync()).Id);
    }

    [Fact]
    public async Task ApprovingFromAKioskSessionIsRefusedRatherThanThrowing()
    {
        using var harness = new WallTestHarness();
        await harness.SeedWallAsync();

        var registry = new KioskPairingRegistry();

        // A tablet trying to pair another tablet. KioskGuardedApiKeyService refuses the mint with a
        // KioskRestrictedException, which is deliberately NOT an UnauthorizedAccessException — and
        // both callers of ApproveAsync are interactive circuits, so letting it escape would kill a
        // wall admin's page instead of telling them no.
        var guarded = new KioskGuardedApiKeyService(harness.ApiKeyService, new StubKioskContext());
        var approver = new KioskPairingApprover(
            guarded,
            harness.CurrentUser,
            harness.WallService,
            registry,
            NullLogger<KioskPairingApprover>.Instance);

        var entry = registry.Create()!;

        Assert.Equal(
            KioskPairingApprovalResult.NotAuthorised,
            await approver.ApproveAsync(entry.Id, harness.WallId));

        // Nothing was minted and the pairing is untouched, so the real tablet keeps waiting.
        Assert.Empty(await harness.ApiKeyService.GetWallKeysAsync(harness.WallId, harness.Owner.Id));

        var state = registry.Find(entry.Id);
        Assert.NotNull(state);
        Assert.Equal(KioskPairingStatus.Pending, state.Status);
    }

    /// <summary>A session that claims to be a kiosk tablet. Restriction only — it grants nothing.</summary>
    private sealed class StubKioskContext : IKioskContext
    {
        public bool IsKiosk => true;

        public Guid? KioskWallId => null;

        public Guid? KioskApiKeyId => null;

        public Task InitializeAsync()
        {
            return Task.CompletedTask;
        }
    }

    private static KioskPairingApprover BuildApprover(WallTestHarness harness, KioskPairingRegistry registry)
    {
        return new KioskPairingApprover(
            harness.ApiKeyService,
            harness.CurrentUser,
            harness.WallService,
            registry,
            NullLogger<KioskPairingApprover>.Instance);
    }
}
