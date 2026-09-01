using Blocwerk.Authentication.Services;
using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Configuration;
using Blocwerk.Core.Services;
using Blocwerk.Web.State;
using NSubstitute;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The in-memory kiosk throttle, and the account-security guard that no kiosk session can talk its
/// way past.
/// </summary>
public class KioskThrottleTests
{
    [Fact]
    public void Throttle_LocksOutAfterTheCap_AndFreesUpAfterTheLockout()
    {
        var registry = new KioskThrottleRegistry();
        var key = KioskThrottleRegistry.PinScopes(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid())[0];
        var start = DateTimeOffset.UtcNow;

        for (var attempt = 0; attempt < KioskThrottleRegistry.MaxAttempts - 1; attempt++)
        {
            registry.RegisterFailure(key, start);
            Assert.False(registry.IsLocked(key, start));
        }

        registry.RegisterFailure(key, start);
        Assert.True(registry.IsLocked(key, start));

        // Still locked while the lockout runs, free once it has passed.
        Assert.True(registry.IsLocked(key, start.Add(KioskThrottleRegistry.Lockout).AddSeconds(-1)));
        Assert.False(registry.IsLocked(key, start.Add(KioskThrottleRegistry.Lockout).AddSeconds(1)));
    }

    [Fact]
    public void Throttle_ForgetsFailuresThatFallOutOfTheWindow()
    {
        var registry = new KioskThrottleRegistry();
        var key = KioskThrottleRegistry.RegistrationScopes("10.0.0.9")[0];
        var start = DateTimeOffset.UtcNow;

        for (var attempt = 0; attempt < KioskThrottleRegistry.MaxAttempts - 1; attempt++)
        {
            registry.RegisterFailure(key, start);
        }

        // A failure after the window starts counting again rather than topping up the old tally, so
        // one mistyped PIN a day never accumulates into a lockout.
        var later = start.Add(KioskThrottleRegistry.Window).AddSeconds(1);
        registry.RegisterFailure(key, later);
        Assert.False(registry.IsLocked(key, later));
    }

    [Fact]
    public void Throttle_IsPerDeviceAndPerTarget()
    {
        var registry = new KioskThrottleRegistry();
        var key = Guid.NewGuid();
        var device = Guid.NewGuid();
        var victim = Guid.NewGuid();
        var bystander = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        for (var attempt = 0; attempt < KioskThrottleRegistry.MaxAttempts; attempt++)
        {
            registry.RegisterFailure(KioskThrottleRegistry.PinScopes(key, device, victim)[0], now);
        }

        Assert.True(registry.IsLocked(KioskThrottleRegistry.PinScopes(key, device, victim)[0], now));

        // Nobody else on the tablet is affected — and, crucially, nothing was written to the user
        // row, so the victim's real account is not locked out anywhere else either.
        Assert.False(registry.IsLocked(KioskThrottleRegistry.PinScopes(key, device, bystander)[0], now));
        Assert.False(registry.IsLocked(
            KioskThrottleRegistry.PinScopes(Guid.NewGuid(), Guid.NewGuid(), victim)[0], now));
    }

    [Fact]
    public void Throttle_IsClearedByASuccess()
    {
        var registry = new KioskThrottleRegistry();
        var key = KioskThrottleRegistry.PinScopes(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid())[0];
        var now = DateTimeOffset.UtcNow;

        for (var attempt = 0; attempt < KioskThrottleRegistry.MaxAttempts; attempt++)
        {
            registry.RegisterFailure(key, now);
        }

        registry.Reset([key]);
        Assert.False(registry.IsLocked(key, now));
    }

    [Fact]
    public async Task AccountSecurityChanges_AreRefusedForAKioskSession()
    {
        var service = BuildCurrentUserService(isKiosk: true);

        await Assert.ThrowsAsync<KioskRestrictedException>(
            () => service.SetPasswordAsync("climber", "hunter2hunter2", null));
        await Assert.ThrowsAsync<KioskRestrictedException>(() => service.BeginTotpEnrollmentAsync());
        await Assert.ThrowsAsync<KioskRestrictedException>(() => service.ConfirmTotpAsync("123456"));
        await Assert.ThrowsAsync<KioskRestrictedException>(() => service.DisableTotpAsync("123456"));
    }

    [Fact]
    public async Task AccountSecurityChanges_AreNotBlockedForAnOrdinarySession()
    {
        var service = BuildCurrentUserService(isKiosk: false);

        // No kiosk refusal: the call proceeds and fails on the missing identity instead, which is
        // exactly the behaviour an ordinary signed-out caller had before kiosk mode existed.
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.BeginTotpEnrollmentAsync());
    }

    private static ICurrentUserService BuildCurrentUserService(bool isKiosk)
    {
        var kioskContext = Substitute.For<IKioskContext>();
        kioskContext.IsKiosk.Returns(isKiosk);

        using var harness = new WallTestHarness();

        return new CurrentUserService(
            new BlocwerkSettings(),
            harness.DbContextFactory,
            Substitute.For<IPasswordLoginService>(),
            Substitute.For<ITotpService>(),
            authenticationStateProvider: null,
            accessor: null,
            kioskContext: kioskContext);
    }
}
