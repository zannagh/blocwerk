using Blocwerk.Core.Telemetry;
using Microsoft.AspNetCore.Components.Server.Circuits;

namespace Blocwerk.Web.State;

/// <summary>
/// Counts live Blazor circuits into the <c>blocwerk.users.connected</c> gauge. One circuit is one
/// active interactive tab, which is the closest signal Blazor Server gives for "someone is using
/// the app right now". Registered per-circuit; the framework calls opened/closed as sockets come
/// and go, including the down/up transitions when a tab briefly loses its connection.
/// </summary>
public sealed class TelemetryCircuitHandler : CircuitHandler
{
    public override Task OnCircuitOpenedAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        BlocwerkMetrics.CircuitOpened();
        return Task.CompletedTask;
    }

    public override Task OnCircuitClosedAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        BlocwerkMetrics.CircuitClosed();
        return Task.CompletedTask;
    }
}
