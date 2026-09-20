using Microsoft.AspNetCore.Components.Server.Circuits;

namespace Blocwerk.Web.State;

/// <summary>
/// Keeps a live circuit's editing leases alive, so that a lease whose circuit is NOT alive expires
/// instead of holding the deploy gate until the process restarts.
/// </summary>
/// <remarks>
/// <para><b>Why a timer and not just inbound activity.</b> What the deploy gate needs proven is
/// "this circuit is alive", not "the user clicked recently". Somebody staring at the alignment
/// editor for six minutes without touching anything must not lose their protection, so the
/// heartbeat is a <see cref="PeriodicTimer"/> on <see cref="EditActivityPolicy.HeartbeatInterval"/>.
/// <see cref="CreateInboundActivityHandler"/> carries the OTHER signal: it is the only place a
/// HUMAN can be observed, and <see cref="EditActivityPolicy.InactivityTimeToLive"/> is measured from
/// it, so a tab abandoned on a gym tablet stops holding the deploy gate even though its socket never
/// dies.</para>
/// <para><b>Why the timer cannot keep a dead client alive.</b> A server-side timer ticks whether or
/// not the browser is reachable, so touching unconditionally would defeat the entire fix. The
/// heartbeat is therefore gated on <see cref="connected"/>, which is driven by
/// <see cref="OnConnectionUpAsync"/>/<see cref="OnConnectionDownAsync"/> — the hub's own view of the
/// client. A half-open socket (a sleeping tablet behind a NAT that never sends a RST) stops sending
/// its keep-alive pings, and the server's <c>ClientTimeoutInterval</c> of 60s closes the connection
/// on that silence alone; that close raises <see cref="OnConnectionDownAsync"/>, the heartbeat stops
/// there, and the lease expires one TTL later. The circuit itself is still retained for five minutes
/// so the user can reconnect — and if they do, <see cref="OnConnectionUpAsync"/> restores the leases
/// immediately, even ones the registry has already dropped, so a reconnected editor is never left
/// unprotected. The lease TTL is derived from the retention period precisely so that window cannot
/// open in the first place; the restore is the belt to its braces.</para>
/// <para>Scoped, so it shares the circuit's <see cref="CircuitEditActivity"/> instance — the same
/// one the editing components take their leases from.</para>
/// </remarks>
public sealed class EditActivityCircuitHandler : CircuitHandler, IAsyncDisposable
{
    private readonly CircuitEditActivity activity;
    private readonly ILogger<EditActivityCircuitHandler> logger;
    private readonly CancellationTokenSource shutdown = new();

    private volatile bool connected;
    private Task? heartbeat;

    public EditActivityCircuitHandler(
        CircuitEditActivity activity,
        ILogger<EditActivityCircuitHandler> logger)
    {
        this.activity = activity;
        this.logger = logger;
    }

    public override Task OnCircuitOpenedAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        heartbeat = RunHeartbeatAsync(shutdown.Token);
        return Task.CompletedTask;
    }

    public override Task OnConnectionUpAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        connected = true;

        // A reconnect: stamp the leases at once rather than waiting up to a full interval, and put
        // back any the registry expired while the socket was down. The connection being up IS the
        // proof the client is there. A lease that expired for want of a HUMAN is not restored — the
        // registry re-checks the entry's own activity history and refuses it.
        activity.Resume();
        return Task.CompletedTask;
    }

    public override Task OnConnectionDownAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        // The hub no longer has a client — either it went away cleanly or it fell silent for longer
        // than ClientTimeoutInterval. Either way this circuit may no longer claim to be alive.
        connected = false;
        return Task.CompletedTask;
    }

    public override Task OnCircuitClosedAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        connected = false;
        shutdown.Cancel();
        return Task.CompletedTask;
    }

    public override Func<CircuitInboundActivityContext, Task> CreateInboundActivityHandler(
        Func<CircuitInboundActivityContext, Task> next)
    {
        return async context =>
        {
            // The user-activity clock. Unlike the heartbeat this is proof a person is there, so it
            // is the only signal that restarts the inactivity TTL — and the only one allowed to
            // resurrect a lease that ran out because nobody was.
            activity.RecordActivity();
            await next(context);
        };
    }

    public async ValueTask DisposeAsync()
    {
        await shutdown.CancelAsync();

        if (heartbeat is not null)
        {
            try
            {
                await heartbeat;
            }
            catch (OperationCanceledException)
            {
                // Expected: the circuit went away.
            }
        }

        shutdown.Dispose();
    }

    private async Task RunHeartbeatAsync(CancellationToken ct)
    {
        // Yield first so circuit startup is never blocked by this loop.
        await Task.Yield();

        using var timer = new PeriodicTimer(EditActivityPolicy.HeartbeatInterval);

        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                if (!connected)
                {
                    continue;
                }

                activity.Touch();
            }
        }
        catch (OperationCanceledException)
        {
            // The circuit closed; its leases are released by CircuitEditActivity's own disposal.
        }
        catch (Exception ex)
        {
            // An unhandled exception from a circuit handler is fatal to the circuit, and a missed
            // heartbeat must never be the thing that takes an editor down. The cost of stopping here
            // is bounded and in the safe direction: the lease expires and the deploy gate opens.
            logger.LogWarning(ex, "The edit-activity heartbeat stopped for this circuit");
        }
    }
}
