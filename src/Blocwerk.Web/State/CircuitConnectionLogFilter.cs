// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Microsoft.AspNetCore.Http.Connections.Features;
using Microsoft.AspNetCore.SignalR;

namespace Blocwerk.Web.State;

/// <summary>
/// Sees what a <see cref="CircuitLifecycleLogHandler"/> cannot: why the SignalR connection under a circuit closed, and a
/// reconnect the server turned down (the client then resumes or reloads into a new circuit). The close reason is handed
/// to the circuit handler through <see cref="CloseReason"/>, so it ends up on the circuit's "connection down" line.
/// Logging only; it changes nothing about the connection.
/// </summary>
public sealed class CircuitConnectionLogFilter : IHubFilter
{
    /// <summary>The close reason of the connection that is going down on the current call chain, if known.</summary>
    public static readonly AsyncLocal<string?> CloseReason = new();

    private readonly ILogger<CircuitConnectionLogFilter> logger;

    public CircuitConnectionLogFilter(ILogger<CircuitConnectionLogFilter> logger)
    {
        this.logger = logger;
    }

    /// <summary>
    /// The reason in words. SignalR reports no error for a close by the client, by a proxy in between and for its own
    /// client timeout, so those can not be told apart here.
    /// </summary>
    public static string Describe(Exception? exception, string? transport) =>
        (exception is null
            ? "no error (closed by client or proxy, or client timeout)"
            : $"{exception.GetType().Name}: {exception.Message}")
        + $", {transport ?? "unknown transport"}";

    public async ValueTask<object?> InvokeMethodAsync(
        HubInvocationContext invocationContext,
        Func<HubInvocationContext, ValueTask<object?>> next)
    {
        var result = await next(invocationContext);
        if (invocationContext.HubMethodName == "ConnectCircuit" && result is false)
        {
            logger.LogInformation(
                "Circuit reconnect rejected on connection {ConnectionId}, the client resumes or reloads into a new circuit",
                invocationContext.Context.ConnectionId);
        }

        return result;
    }

    public async Task OnDisconnectedAsync(
        HubLifetimeContext context,
        Exception? exception,
        Func<HubLifetimeContext, Exception?, Task> next)
    {
        var transport = context.Context.Features.Get<IHttpTransportFeature>()?.TransportType.ToString();
        CloseReason.Value = Describe(exception, transport);
        try
        {
            await next(context, exception);
        }
        finally
        {
            CloseReason.Value = null;
        }
    }
}
