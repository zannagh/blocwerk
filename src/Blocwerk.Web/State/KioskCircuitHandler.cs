using Blocwerk.Authentication.Kiosk;
using Blocwerk.Core.Abstractions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server.Circuits;

namespace Blocwerk.Web.State;

/// <summary>
/// Resolves the circuit's <see cref="IKioskContext"/> when the circuit opens, and then keeps the
/// kiosk session honest for as long as the circuit lives.
/// </summary>
/// <remarks>
/// <para><b>Priming.</b> This is the answer to the null-<c>HttpContext</c> hazard. Circuit handlers
/// are built from the circuit's own service scope at circuit creation — while the connection's HTTP
/// context is still on the stack — so priming here captures the kiosk device cookie at the one
/// instant it is guaranteed readable. Every later read inside the circuit, including the ones that
/// stamp the database contexts, is served from that captured value.</para>
/// <para><b>Revalidation, and why it has to live here.</b> <see cref="KioskSessionValidator"/> runs
/// in <c>OnValidatePrincipal</c>, which only fires on an HTTP REQUEST. A live circuit makes none:
/// identity comes from <c>CurrentUserService</c>'s cached user and the authentication state
/// provider's cached state, and neither is ever re-read. So without this, a kiosk session that had
/// been idle for hours — or whose key had been revoked, or whose member had withdrawn consent —
/// still executed every interactive write for as long as the websocket stayed up. The client-side
/// <c>kiosk-idle.js</c> was the only thing standing there, and it was explicitly written to be a
/// courtesy timer rather than a gate.</para>
/// <para><b>The seam.</b> <see cref="CreateInboundActivityHandler"/> wraps every piece of inbound
/// circuit activity — every UI event and every JS-to-.NET call — so it is both a true activity clock
/// (the idle window now measures real in-circuit use, not just HTTP traffic) and the one place where
/// refusing is safe: it runs on the circuit's dispatcher, so navigating away from it is legal.
/// Nothing else in a circuit offers both.</para>
/// <para><b>The timer alongside it.</b> An ABANDONED circuit produces no inbound activity at all, so
/// the handler above would never fire for it. The timer covers that case: it re-reads the key and
/// the consent on <see cref="KioskCircuitPolicy.RevalidationInterval"/> and latches the session
/// dead. It deliberately does NOT navigate — <c>NavigationManager</c> is not safe to drive from a
/// background thread — which costs nothing, because a circuit nobody is touching has nobody to
/// redirect; the latch makes the next touch fail.</para>
/// <para><b>A sleeping tablet.</b> When the screen sleeps the websocket drops and the framework
/// disposes the circuit and its scope, which cancels the timer through
/// <see cref="OnCircuitClosedAsync"/> and <see cref="DisposeAsync"/>. Waking up starts a fresh
/// circuit from a fresh HTTP request, where the cookie validator has already had its say.</para>
/// <para><b>Ordinary sessions.</b> Everything below is behind <c>actingKioskSession</c>, which is set
/// only for a principal carrying the kiosk claims. A normal login runs the unmodified pass-through
/// and starts no timer.</para>
/// </remarks>
public sealed class KioskCircuitHandler : CircuitHandler, IAsyncDisposable
{
    private readonly IKioskContext kioskContext;
    private readonly AuthenticationStateProvider authenticationStateProvider;
    private readonly KioskKeyValidator keyValidator;
    private readonly ICurrentUserService currentUserService;
    private readonly NavigationManager navigationManager;
    private readonly ILogger<KioskCircuitHandler> logger;

    private readonly CancellationTokenSource shutdown = new();
    private readonly SemaphoreSlim revalidationGate = new(1, 1);

    private bool actingKioskSession;
    private Guid keyId;
    private Guid wallId;
    private Guid userId;

    private DateTimeOffset lastActivity;
    private DateTimeOffset lastRevalidated = DateTimeOffset.MinValue;
    private volatile bool credentialsRevoked;
    private volatile bool ended;
    private Task? watchdog;

    public KioskCircuitHandler(
        IKioskContext kioskContext,
        AuthenticationStateProvider authenticationStateProvider,
        KioskKeyValidator keyValidator,
        ICurrentUserService currentUserService,
        NavigationManager navigationManager,
        ILogger<KioskCircuitHandler> logger)
    {
        this.kioskContext = kioskContext;
        this.authenticationStateProvider = authenticationStateProvider;
        this.keyValidator = keyValidator;
        this.currentUserService = currentUserService;
        this.navigationManager = navigationManager;
        this.logger = logger;
    }

    public override async Task OnCircuitOpenedAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        await kioskContext.InitializeAsync();

        var state = await authenticationStateProvider.GetAuthenticationStateAsync();
        var principal = state.User;
        if (!principal.IsKioskPrincipal())
        {
            return;
        }

        var claimKeyId = principal.ReadGuid(KioskClaims.KeyId);
        var claimWallId = principal.ReadGuid(KioskClaims.WallId);
        var claimUserId = principal.ReadGuid("uid");

        if (claimKeyId is null || claimWallId is null || claimUserId is null)
        {
            // A kiosk principal we cannot bound. The cookie validator drops these on the next HTTP
            // request; in-circuit, latch it dead so the first interaction ends the session.
            actingKioskSession = true;
            credentialsRevoked = true;
            lastActivity = DateTimeOffset.UtcNow;
            return;
        }

        actingKioskSession = true;
        keyId = claimKeyId.Value;
        wallId = claimWallId.Value;
        userId = claimUserId.Value;

        // Start the idle clock where the cookie left it, not at circuit open: a circuit re-established
        // after a reconnect must not silently hand the session another thirty minutes.
        lastActivity = principal.ReadLastSeen() ?? DateTimeOffset.UtcNow;

        watchdog = RunWatchdogAsync(shutdown.Token);
    }

    public override Task OnCircuitClosedAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        shutdown.Cancel();
        return Task.CompletedTask;
    }

    public override Func<CircuitInboundActivityContext, Task> CreateInboundActivityHandler(
        Func<CircuitInboundActivityContext, Task> next)
    {
        return async context =>
        {
            if (!actingKioskSession)
            {
                await next(context);
                return;
            }

            var now = DateTimeOffset.UtcNow;

            if (!ended && !credentialsRevoked && KioskCircuitPolicy.ShouldRevalidate(lastRevalidated, now))
            {
                await RevalidateAsync(now, shutdown.Token);
            }

            if (ended || KioskCircuitPolicy.ShouldEndSession(actingKioskSession, lastActivity, now, credentialsRevoked))
            {
                // The activity is DROPPED, not forwarded: whatever the user was trying to do does not
                // run. Ending the session is the only thing that happens.
                await EndSessionAsync();
                return;
            }

            lastActivity = now;
            await next(context);
        };
    }

    public async ValueTask DisposeAsync()
    {
        await shutdown.CancelAsync();

        if (watchdog is not null)
        {
            try
            {
                await watchdog;
            }
            catch (OperationCanceledException)
            {
                // Expected: the circuit went away.
            }
        }

        shutdown.Dispose();
        revalidationGate.Dispose();
    }

    private async Task RunWatchdogAsync(CancellationToken ct)
    {
        // Yield first so circuit startup is never blocked by this loop.
        await Task.Yield();

        using var timer = new PeriodicTimer(KioskCircuitPolicy.RevalidationInterval);

        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                if (ended || credentialsRevoked)
                {
                    return;
                }

                await RevalidateAsync(DateTimeOffset.UtcNow, ct);
            }
        }
        catch (OperationCanceledException)
        {
            // The circuit closed. Nothing to clean up beyond what DisposeAsync does.
        }
        catch (Exception ex)
        {
            // An unhandled exception from a circuit handler is fatal to the circuit, and failing to
            // re-validate must never be the thing that takes a wall's tablet down. Fail CLOSED
            // instead: latch the session dead and let the next interaction end it.
            credentialsRevoked = true;
            logger.LogWarning(ex, "Kiosk circuit revalidation failed for wall {WallId}; ending the session", wallId);
        }
    }

    private async Task RevalidateAsync(DateTimeOffset now, CancellationToken ct)
    {
        // The timer and an inbound activity can arrive together; one query is enough for both.
        if (!await revalidationGate.WaitAsync(TimeSpan.Zero, ct))
        {
            return;
        }

        try
        {
            if (!KioskCircuitPolicy.ShouldRevalidate(lastRevalidated, now))
            {
                return;
            }

            var valid = await keyValidator.IsKeyValidAsync(keyId, wallId, ct)
                        && await keyValidator.HasConsentAsync(wallId, userId, ct);

            lastRevalidated = now;

            if (!valid)
            {
                credentialsRevoked = true;
                logger.LogInformation(
                    "Kiosk key {ApiKeyId} or consent for user {UserId} on wall {WallId} is gone; ending the live session",
                    keyId,
                    userId,
                    wallId);
            }
        }
        finally
        {
            revalidationGate.Release();
        }
    }

    private async Task EndSessionAsync()
    {
        if (ended)
        {
            return;
        }

        ended = true;
        await shutdown.CancelAsync();

        // Drop the identity the circuit had cached, so anything that still runs before the browser
        // has navigated cannot resolve the acting user from it.
        currentUserService.InvalidateCache();

        logger.LogInformation(
            "Ending live kiosk session for user {UserId} on wall {WallId} (revoked: {Revoked})",
            userId,
            wallId,
            credentialsRevoked);

        try
        {
            // A FULL page load, not a circuit navigation: the identity being discarded lives in a
            // cookie, and only a real request re-runs the sign-out and the cookie validator. The
            // device registration survives, so the tablet lands back on its own wall as an anonymous
            // kiosk rather than as a stranger's browser.
            navigationManager.NavigateTo("/account/logout", forceLoad: true);
        }
        catch (Exception ex)
        {
            // The activity has already been dropped and the identity already invalidated, so the
            // session is refused whether or not the browser follows us.
            logger.LogWarning(ex, "Could not redirect the ended kiosk session on wall {WallId}", wallId);
        }
    }
}
